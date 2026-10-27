[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repoRoot 'artifacts'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($path in @($artifacts, $package)) {
    for ($ancestor = Get-Item -LiteralPath $path; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'El laboratorio no admite redirecciones en sus rutas.'
        }
    }
}
if (-not $package.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Seleccione el publish-win-x64 de un gate local dentro de artifacts.'
}
$manifest = Get-Content -LiteralPath (Join-Path $package 'build-manifest.json') -Raw | ConvertFrom-Json
$executable = Join-Path $package 'Ticketfy.Desktop.exe'
$verified = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $package) 'startup-verified.json') -Raw | ConvertFrom-Json
if (-not $verified.Succeeded -or $verified.ExecutableSha256 -ne (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -or
    $verified.AssemblySha256 -ne (Get-FileHash -LiteralPath (Join-Path $package 'Ticketfy.Desktop.dll') -Algorithm SHA256).Hash) {
    throw 'El paquete no tiene una verificación de arranque vigente.'
}
if ($manifest.product -ne 'Ticketfy.Desktop' -or $manifest.runtime -ne 'win-x64' -or
    $manifest.purpose -ne 'deterministic-ci-payload' -or -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'El paquete no es un candidato local reconocido.'
}
$labParent = Join-Path $artifacts 'internal-labs'
if (Test-Path -LiteralPath $labParent) {
    if ((Get-Item -LiteralPath $labParent).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'El laboratorio no admite redirecciones de carpeta.'
    }
}
else { New-Item -ItemType Directory -Path $labParent | Out-Null }
$lab = Join-Path $labParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $lab | Out-Null
$data = Join-Path $lab 'Data'
New-Item -ItemType Directory -Path $data | Out-Null
# Only a new, empty data directory is selected. This script never copies, opens or migrates a database.
$start = @'
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'laboratory.json') -Raw | ConvertFrom-Json
$data = Join-Path $PSScriptRoot 'Data'
foreach ($directory in @($PSScriptRoot, $data)) {
    for ($ancestor = Get-Item -LiteralPath $directory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'No inicie un laboratorio con una carpeta redirigida.'
        }
    }
}
if ((Get-FileHash -LiteralPath $config.executable -Algorithm SHA256).Hash -ne $config.executable_sha256) {
    throw 'El ejecutable cambió. Prepare otro laboratorio para conservar la trazabilidad.'
}
if ((Get-FileHash -LiteralPath $config.assembly -Algorithm SHA256).Hash -ne $config.assembly_sha256) {
    throw 'La aplicación cambió. Prepare otro laboratorio para conservar la trazabilidad.'
}
Write-Host "LABORATORIO: datos sintéticos en $data"
& $config.executable --data-directory $data
'@
[IO.File]::WriteAllText((Join-Path $lab 'Start-Laboratory.ps1'), $start, [Text.UTF8Encoding]::new($false))
$record = [ordered]@{
    purpose = 'internal-synthetic-testing'
    executable = $executable
    executable_sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    assembly = Join-Path $package 'Ticketfy.Desktop.dll'
    assembly_sha256 = (Get-FileHash -LiteralPath (Join-Path $package 'Ticketfy.Desktop.dll') -Algorithm SHA256).Hash
    package_manifest = $manifest
    data_directory = $data
    created_utc = [DateTimeOffset]::UtcNow.ToString('O')
}
[IO.File]::WriteAllText((Join-Path $lab 'laboratory.json'), ($record | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Launcher = Join-Path $lab 'Start-Laboratory.ps1'; DataDirectory = $data; Package = $package }
