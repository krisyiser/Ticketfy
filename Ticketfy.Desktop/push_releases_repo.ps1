param (
    [string]$Version = "",
    [string]$ReleasesDir = (Join-Path $PSScriptRoot "Output\Releases"),
    [string]$GitDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "TicketfyReleasesRepo"),
    [string]$RemoteUrl = "https://git.valcore/yersi/ticketfy-releases.git"
)

$ErrorActionPreference = "Stop"
throw "Publicación remota deshabilitada: Forgejo está en mantenimiento y el proyecto opera en modo local."

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$ProjectXml = Get-Content (Join-Path $PSScriptRoot "Ticketfy.Desktop.csproj")
    $Version = [string](($ProjectXml.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version)
    $Version = $Version.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use the numeric major.minor.patch format."
}
if (Test-Path $GitDir) {
    Remove-Item -Recurse -Force $GitDir
}
New-Item -ItemType Directory -Path $GitDir | Out-Null

Write-Host "Copiando binarios e instalador v$Version..."
if (Test-Path "$ReleasesDir\Ticketfy-Setup-v$Version-x64.exe") {
    Copy-Item "$ReleasesDir\Ticketfy-Setup-v$Version-x64.exe" "$GitDir\" -Force
}
if (Test-Path "$ReleasesDir\Ticketfy-Setup-v$Version-x86.exe") {
    Copy-Item "$ReleasesDir\Ticketfy-Setup-v$Version-x86.exe" "$GitDir\" -Force
}
if (Test-Path "$ReleasesDir\Ticketfy-Instalador-v$Version-x64.zip") {
    Copy-Item "$ReleasesDir\Ticketfy-Instalador-v$Version-x64.zip" "$GitDir\" -Force
}
if (Test-Path "$ReleasesDir\Ticketfy-Portable-v$Version-x64.zip") {
    Copy-Item "$ReleasesDir\Ticketfy-Portable-v$Version-x64.zip" "$GitDir\" -Force
}
if (Test-Path "$ReleasesDir\releases.json") {
    Copy-Item "$ReleasesDir\releases.json" "$GitDir\" -Force
}

Set-Location $GitDir
git init
git remote add origin $RemoteUrl
git add .
git commit -m "release: v$Version"
git push origin HEAD:refs/heads/main
Write-Host "¡Repositorio git.valcore/yersi/ticketfy-releases actualizado exitosamente a v$Version!" -ForegroundColor Green

