$ErrorActionPreference = 'Stop'
$runtimePath = Join-Path $PSScriptRoot 'runtime'
New-Item -ItemType Directory -Force -Path $runtimePath | Out-Null
$packagePath = Join-Path $runtimePath 'nlog.zip'
$extractPath = Join-Path $runtimePath 'nlog-package'
Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/nlog/4.7.15/nlog.4.7.15.nupkg' -OutFile $packagePath
Expand-Archive -LiteralPath $packagePath -DestinationPath $extractPath -Force
$vendorPath = Join-Path $runtimePath 'vendor'
New-Item -ItemType Directory -Force -Path $vendorPath | Out-Null
Copy-Item -LiteralPath (Join-Path $extractPath 'lib/net45/NLog.dll') -Destination (Join-Path $vendorPath 'NLog.dll')
Write-Output 'PWLib satellite dependency installed: NLog 4.7.15 (NuGet).'
