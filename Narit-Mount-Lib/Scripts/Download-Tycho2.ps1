# Tycho-2 Catalog Downloader
# Downloads and extracts Tycho-2 catalog (2.5 million stars)
# Total size: ~300 MB compressed, ~950 MB uncompressed

param(
    [string]$TargetPath = "C:\Users\Fork Mount PC\Documents\Kepler\Tycho2"
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Tycho-2 Catalog Downloader" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$baseUrl = "https://cdsarc.cds.unistra.fr/ftp/cats/I/259"
$totalFiles = 20
$files = @(
    "tyc2.dat.00.gz",
    "tyc2.dat.01.gz",
    "tyc2.dat.02.gz",
    "tyc2.dat.03.gz",
    "tyc2.dat.04.gz",
    "tyc2.dat.05.gz",
    "tyc2.dat.06.gz",
    "tyc2.dat.07.gz",
    "tyc2.dat.08.gz",
    "tyc2.dat.09.gz",
    "tyc2.dat.10.gz",
    "tyc2.dat.11.gz",
    "tyc2.dat.12.gz",
    "tyc2.dat.13.gz",
    "tyc2.dat.14.gz",
    "tyc2.dat.15.gz",
    "tyc2.dat.16.gz",
    "tyc2.dat.17.gz",
    "tyc2.dat.18.gz",
    "tyc2.dat.19.gz"
)

# Create target directory
Write-Host "[SETUP] Target directory: $TargetPath" -ForegroundColor Yellow
if (-not (Test-Path $TargetPath)) {
    New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null
    Write-Host "[OK] Created directory: $TargetPath" -ForegroundColor Green
} else {
    Write-Host "[OK] Directory exists" -ForegroundColor Green
}
Write-Host ""

# Download files
$downloadedCount = 0
$skippedCount = 0
$failedCount = 0

for ($i = 0; $i -lt $files.Count; $i++) {
    $file = $files[$i]
    $fileNum = $i
    $url = "$baseUrl/$file"
    $gzPath = Join-Path $TargetPath $file
    $datPath = $gzPath -replace '\.gz$', ''
    
    Write-Host "[$($i+1)/$totalFiles] Processing: $file" -ForegroundColor Cyan
    
    # Check if uncompressed file already exists
    if (Test-Path $datPath) {
        $fileSize = (Get-Item $datPath).Length / 1MB
        Write-Host "  [SKIP] Already extracted: $([math]::Round($fileSize, 1)) MB" -ForegroundColor Yellow
        $skippedCount++
        continue
    }
    
    # Check if compressed file exists
    if (Test-Path $gzPath) {
        Write-Host "  [FOUND] Compressed file exists, extracting..." -ForegroundColor Yellow
    } else {
        # Download
        Write-Host "  [DOWNLOAD] From: $url" -ForegroundColor White
        try {
            $progressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $url -OutFile $gzPath -TimeoutSec 300
            $fileSize = (Get-Item $gzPath).Length / 1MB
            Write-Host "  [OK] Downloaded: $([math]::Round($fileSize, 1)) MB" -ForegroundColor Green
            $downloadedCount++
        }
        catch {
            Write-Host "  [ERROR] Download failed: $_" -ForegroundColor Red
            $failedCount++
            continue
        }
    }
    
    # Extract .gz file
    Write-Host "  [EXTRACT] Decompressing..." -ForegroundColor White
    try {
        # Use .NET GZipStream for extraction
        $inputStream = New-Object System.IO.FileStream $gzPath, ([System.IO.FileMode]::Open)
        $outputStream = New-Object System.IO.FileStream $datPath, ([System.IO.FileMode]::Create)
        $gzipStream = New-Object System.IO.Compression.GZipStream $inputStream, ([System.IO.Compression.CompressionMode]::Decompress)
        
        $buffer = New-Object byte[](1024 * 1024) # 1MB buffer
        $totalBytes = 0
        
        while ($true) {
            $read = $gzipStream.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) { break }
            $outputStream.Write($buffer, 0, $read)
            $totalBytes += $read
        }
        
        $gzipStream.Close()
        $outputStream.Close()
        $inputStream.Close()
        
        $extractedSize = $totalBytes / 1MB
        Write-Host "  [OK] Extracted: $([math]::Round($extractedSize, 1)) MB" -ForegroundColor Green
        
        # Delete compressed file to save space
        Remove-Item $gzPath -Force
        Write-Host "  [CLEANUP] Removed compressed file" -ForegroundColor Gray
    }
    catch {
        Write-Host "  [ERROR] Extraction failed: $_" -ForegroundColor Red
        $failedCount++
        continue
    }
    
    Write-Host ""
}

# Download ReadMe file
Write-Host "[INFO] Downloading ReadMe file..." -ForegroundColor Cyan
$readmeUrl = "$baseUrl/ReadMe"
$readmePath = Join-Path $TargetPath "ReadMe.txt"
try {
    Invoke-WebRequest -Uri $readmeUrl -OutFile $readmePath -TimeoutSec 60
    Write-Host "[OK] ReadMe downloaded" -ForegroundColor Green
}
catch {
    Write-Host "[WARNING] Could not download ReadMe: $_" -ForegroundColor Yellow
}
Write-Host ""

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Installation Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Target Path: $TargetPath" -ForegroundColor White
Write-Host "Downloaded:  $downloadedCount files" -ForegroundColor $(if ($downloadedCount -gt 0) { "Green" } else { "Gray" })
Write-Host "Skipped:     $skippedCount files (already extracted)" -ForegroundColor $(if ($skippedCount -gt 0) { "Yellow" } else { "Gray" })
Write-Host "Failed:      $failedCount files" -ForegroundColor $(if ($failedCount -gt 0) { "Red" } else { "Gray" })
Write-Host ""

# Verify installation
$extractedFiles = Get-ChildItem -Path $TargetPath -Filter "tyc2.dat.*" | Where-Object { $_.Extension -ne ".gz" }
$extractedCount = $extractedFiles.Count
$totalSize = ($extractedFiles | Measure-Object -Property Length -Sum).Sum / 1MB

Write-Host "Verification:" -ForegroundColor Cyan
Write-Host "  Extracted files: $extractedCount / $totalFiles" -ForegroundColor White
Write-Host "  Total size:      $([math]::Round($totalSize, 1)) MB" -ForegroundColor White

if ($extractedCount -eq $totalFiles) {
    Write-Host ""
    Write-Host "[SUCCESS] Tycho-2 catalog installation complete!" -ForegroundColor Green
    Write-Host "[INFO] You can now use Tycho2Reader in your code" -ForegroundColor Green
    Write-Host ""
    Write-Host "Example:" -ForegroundColor Yellow
    Write-Host '  var reader = new Tycho2Reader(@"' + $TargetPath + '");' -ForegroundColor Gray
    Write-Host '  var stars = reader.GetStarsInRegion(ra, dec, radius, maxMag);' -ForegroundColor Gray
} else {
    Write-Host ""
    Write-Host "[WARNING] Installation incomplete: $extractedCount / $totalFiles files" -ForegroundColor Yellow
    Write-Host "[TIP] Run this script again to retry failed downloads" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Press any key to exit..." -ForegroundColor Gray
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
