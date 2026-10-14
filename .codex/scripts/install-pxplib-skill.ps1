[CmdletBinding()]
param(
    [string]$TargetSkillsRoot
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoCodexDir = Split-Path -Parent $scriptDir
$sourceSkillDir = Join-Path $repoCodexDir "skills\pxplib-pointing-model"

if (-not (Test-Path -LiteralPath $sourceSkillDir)) {
    throw "Source skill not found: $sourceSkillDir"
}

if ([string]::IsNullOrWhiteSpace($TargetSkillsRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        $TargetSkillsRoot = Join-Path $env:CODEX_HOME "skills"
    } else {
        $TargetSkillsRoot = Join-Path $env:USERPROFILE ".codex\skills"
    }
}

$targetSkillDir = Join-Path $TargetSkillsRoot "pxplib-pointing-model"

New-Item -ItemType Directory -Path $TargetSkillsRoot -Force | Out-Null

if (Test-Path -LiteralPath $targetSkillDir) {
    Remove-Item -LiteralPath $targetSkillDir -Recurse -Force
}

Copy-Item -LiteralPath $sourceSkillDir -Destination $targetSkillDir -Recurse -Force

Write-Host "Installed skill to: $targetSkillDir"
Write-Host "You can invoke it as: `$pxplib-pointing-model"
