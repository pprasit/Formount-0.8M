param([Parameter(Mandatory=$true)][string]$Server)
$ErrorActionPreference='Stop'
try {
 & sc.exe config w32time start= auto | Out-Null
 if($LASTEXITCODE -ne 0){exit 1}
 & sc.exe start w32time | Out-Null
 & w32tm.exe /config "/manualpeerlist:$Server,0x8" /syncfromflags:manual /update | Out-Null
 if($LASTEXITCODE -ne 0){exit 2}
 & w32tm.exe /resync /rediscover | Out-Null
 # Synchronization can settle asynchronously; persisted configuration is verified by Python.
 exit 0
} catch {exit 3}
