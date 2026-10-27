[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = Join-Path $repo 'artifacts'
if (-not $output.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) { throw 'Use una carpeta nueva dentro de artifacts para conservar las evidencias.' }
foreach ($path in @($package, $output)) {
    for ($ancestor = [IO.DirectoryInfo]::new($path); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'No se admiten carpetas redirigidas.' }
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $package 'build-manifest.json') -Raw | ConvertFrom-Json
$verified = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $package) 'startup-verified.json') -Raw | ConvertFrom-Json
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or (git -C $repo status --porcelain) -or $manifest.source_state -ne 'clean' -or $manifest.commit -ne $commit) {
    throw 'El instalador compartible exige el paquete de un commit limpio actual.'
}
if ($manifest.product -ne 'Ticketfy.Desktop' -or $manifest.runtime -ne 'win-x64' -or
    $manifest.purpose -ne 'deterministic-ci-payload' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'El paquete no es un candidato Windows x64 reconocido.'
}
if (-not $verified.Succeeded -or
    $verified.ExecutableSha256 -ne (Get-FileHash -LiteralPath (Join-Path $package 'Ticketfy.Desktop.exe')).Hash -or
    $verified.AssemblySha256 -ne (Get-FileHash -LiteralPath (Join-Path $package 'Ticketfy.Desktop.dll')).Hash) {
    throw 'El paquete no tiene una comprobación de arranque vigente.'
}
$runtime = Get-Content -LiteralPath (Join-Path $package 'Ticketfy.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')) {
    throw 'El candidato debe incluir .NET para funcionar sin descargas adicionales.'
}
$sourceFiles = @(Get-ChildItem -LiteralPath $package -Recurse -File)
foreach ($file in $sourceFiles) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Name -match '\.(db(?:-wal|-shm)?|dpapi|jwt|pem|pfx|log|trx)$' -or
        $file.DirectoryName -match '[\\/](Database|Secrets|Credentials|Backups|Logs)([\\/]|$)') {
        throw "El paquete contiene datos o archivos ajenos a una distribución limpia: $($file.Name)"
    }
}
if (-not $CompilerPath) {
    $CompilerPath = @(
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $CompilerPath) { throw 'Instale Inno Setup 6.4 o posterior, o indique CompilerPath.' }
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$payload = Join-Path $output 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
foreach ($file in $sourceFiles) {
    $target = Join-Path $payload ([IO.Path]::GetRelativePath($package, $file.FullName))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
[IO.File]::WriteAllText((Join-Path $payload 'ticketfy-internal-testing.txt'), "Ticketfy.InternalTesting.v1`n", [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LEEME-PRUEBAS.txt') -Destination $payload
$results = Join-Path $output 'verification'
New-Item -ItemType Directory -Path $results | Out-Null
& (Join-Path $PSScriptRoot 'assert-packaged-startup.ps1') -PackageDirectory $payload -ResultsDirectory $results
$probeIdentity = Get-Content -LiteralPath (Join-Path $output 'startup-verified.json') -Raw | ConvertFrom-Json
$probe = Get-Content -LiteralPath $probeIdentity.ReportPath -Raw | ConvertFrom-Json
if (-not $probe.InternalTestingEdition) { throw 'El binario no reconoce el aislamiento de la edición de pruebas.' }
$shortCommit = $commit.Substring(0, 12)
& $CompilerPath /Qp "/DMyAppVersion=$($manifest.version)" "/DMySourceDir=$payload" "/DMyOutputDir=$output" "/DMyCommit=$shortCommit" (Join-Path $PSScriptRoot 'internal-testing.iss') | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Inno Setup falló con código $LASTEXITCODE." }
$installer = Join-Path $output "Ticketfy-Pruebas-$($manifest.version)-$shortCommit-win-x64-Setup.exe"
$hash = (Get-FileHash -LiteralPath $installer).Hash
[IO.File]::WriteAllText($installer + '.sha256', "$hash  $([IO.Path]::GetFileName($installer))`n", [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LEEME-PRUEBAS.txt') -Destination $output
$inventory = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($payload, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash; bytes = $_.Length }
})
$record = [ordered]@{
    purpose = 'internal-testing-installer'; commit = $commit; version = $manifest.version
    source_state = 'clean'; signed = $false; installer = [IO.Path]::GetFileName($installer); sha256 = $hash
    architecture = 'x64'; minimum_windows_build = 19045; included_runtime = $manifest.runtime_version
    compiler_sha256 = (Get-FileHash -LiteralPath $CompilerPath).Hash
    facturama_enabled = $false; contains_user_data = $false; payload = $inventory
    qualification = 'Requiere aceptación en los equipos de destino; no es una aprobación comercial.'
}
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'installer-manifest.json') -Encoding utf8
$bundle = Join-Path $output "Ticketfy-Pruebas-$($manifest.version)-$shortCommit-win-x64-Compartir.zip"
Compress-Archive -LiteralPath @($installer, ($installer + '.sha256'), (Join-Path $output 'LEEME-PRUEBAS.txt'), (Join-Path $output 'installer-manifest.json')) -DestinationPath $bundle
if (git -C $repo status --porcelain) { throw 'El empaquetado modificó el checkout.' }
[pscustomobject]@{ Installer = $installer; Sha256 = $hash; ShareBundle = $bundle; OutputDirectory = $output }
