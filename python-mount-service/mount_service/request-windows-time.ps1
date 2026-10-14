param([Parameter(Mandatory=$true)][string]$Server)
$ErrorActionPreference='Stop'
try {
 $helper=Join-Path $PSScriptRoot 'configure-windows-time.ps1'
 $process=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -WindowStyle Normal -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$helper+'"'),'-Server',$Server) -Wait -PassThru
 exit $process.ExitCode
} catch {exit 4}
