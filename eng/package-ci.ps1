[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/package",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "Ticketfy.Desktop/Ticketfy.Desktop.csproj"
$output = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
if (-not $output.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "El directorio de salida debe estar dentro del repositorio."
}

if (Test-Path -LiteralPath $output) {
    throw "El directorio de salida ya existe. Elija uno nuevo para conservar la evidencia: $output"
}
$publish = Join-Path $output "publish-win-x64"
New-Item -ItemType Directory -Path $publish -Force | Out-Null

$restoreArgument = if ($NoRestore) { "--no-restore" } else { $null }
$publishArguments = @(
    "publish", $project,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--output", $publish,
    "-p:ContinuousIntegrationBuild=true",
    "-p:Deterministic=true",
    "-p:DebugType=embedded",
    "-p:PathMap=$repoRoot=/_/src"
)
if ($restoreArgument) { $publishArguments += $restoreArgument }
& dotnet @publishArguments | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló con código $LASTEXITCODE." }

[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "El proyecto no define Version." }
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "No se pudo determinar el commit del paquete." }
$sourceState = if (& git -C $repoRoot status --porcelain) { "dirty" } else { "clean" }
$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw "No se pudo determinar el SDK del paquete." }
$targetFramework = [string]($projectXml.Project.PropertyGroup | Where-Object { $_.TargetFramework } | Select-Object -First 1).TargetFramework
$runtimeConfig = Get-Content (Join-Path $publish "Ticketfy.Desktop.runtimeconfig.json") -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq "Microsoft.NETCore.App").version
if ([string]::IsNullOrWhiteSpace($runtimeVersion)) { throw "El paquete debe incluir el runtime .NET." }

$manifest = [ordered]@{
    product = "Ticketfy.Desktop"
    version = $version
    runtime = "win-x64"
    target_framework = $targetFramework
    sdk = $sdkVersion
    runtime_version = $runtimeVersion
    commit = $commit
    source_state = $sourceState
    signed = $false
    purpose = "deterministic-ci-payload"
}
$manifestPath = Join-Path $publish "build-manifest.json"
[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json) + "`n",
    [Text.UTF8Encoding]::new($false))

$zipPath = Join-Path $output "Ticketfy.Desktop-$version-win-x64-ci.zip"
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $fixedTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        Get-ChildItem -LiteralPath $publish -File -Recurse |
            Sort-Object { [IO.Path]::GetRelativePath($publish, $_.FullName) } |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp
                $entryStream = $entry.Open()
                try {
                    $fileStream = [IO.File]::OpenRead($_.FullName)
                    try { $fileStream.CopyTo($entryStream) } finally { $fileStream.Dispose() }
                }
                finally { $entryStream.Dispose() }
            }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
[IO.File]::WriteAllText($zipPath + '.sha256', "$hash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Paquete CI: $zipPath"
Write-Host "SHA256: $hash"
[pscustomobject]@{ Path = $zipPath; Sha256 = $hash; Version = $version }
