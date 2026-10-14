param([int]$Port = 8765)
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$pythonExe = Join-Path (Split-Path $projectDir -Parent) '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonExe)) { throw 'Create the repository .venv and install this package first. See README.md.' }
New-Item -ItemType Directory -Force -Path (Join-Path $projectDir 'runtime') | Out-Null
$listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($listener) {
    $health = Invoke-RestMethod "http://127.0.0.1:$Port/api/health"
    if ($health.service -ne 'narit-mount-service') { throw "Port $Port is already in use by another service." }
    Write-Output "Mount Service already running: http://localhost:$Port"
    exit
}
$process = Start-Process -FilePath $pythonExe -ArgumentList @('-m','uvicorn','mount_service.api:app','--host','0.0.0.0','--port',"$Port") -WorkingDirectory $projectDir -WindowStyle Hidden -RedirectStandardOutput (Join-Path $projectDir 'runtime\server.out.log') -RedirectStandardError (Join-Path $projectDir 'runtime\server.err.log') -PassThru
$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 200
    try {
        $health = Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 1
        if ($health.service -eq 'narit-mount-service') { $ready = $true; break }
    } catch { }
}
if (-not $ready) { throw 'Server did not become ready. Check runtime/server.err.log.' }
$serverProcessId = (Get-NetTCPConnection -LocalPort $Port -State Listen | Select-Object -First 1).OwningProcess
$serverProcessId | Set-Content (Join-Path $projectDir 'runtime\server.pid')
Write-Output "Mount Service ready (PID $serverProcessId): http://localhost:$Port"
