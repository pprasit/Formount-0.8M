$Host.UI.RawUI.WindowTitle = 'NARIT AI - API Key Setup'
Write-Host 'NARIT AI API KEY SETUP' -ForegroundColor Cyan
Write-Host 'Paste the API key here and press Enter. Characters will be hidden.'
$ErrorActionPreference = 'Stop'
$credentialDirectory = Join-Path $env:LOCALAPPDATA 'NARIT-MountController'
New-Item -ItemType Directory -Path $credentialDirectory -Force | Out-Null
$credentialPath = Join-Path $credentialDirectory 'lllm-key.dpapi'
$secret = Read-Host 'Enter your NARIT AI API key (hidden; do not paste it into chat)' -AsSecureString
if ($secret.Length -eq 0) { throw 'No key entered; nothing saved.' }
$secret | ConvertFrom-SecureString | Set-Content -LiteralPath $credentialPath -Encoding ascii
$secret.Dispose()
Remove-Variable secret
Write-Host 'NARIT AI credential saved securely for this Windows user. The key was not displayed.'
