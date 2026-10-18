[CmdletBinding()]
param(
    [switch]$LockedRestore,
    [switch]$RequireClean,
    [switch]$VerifyReproducible
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repoRoot
try {
    if ($RequireClean -and (git status --porcelain)) {
        throw "La canalización reproducible requiere un checkout limpio."
    }

    $locked = if ($LockedRestore) { "--locked-mode" } else { $null }
    $restoreArgs = @("restore", "Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj", "-p:RuntimeIdentifier=win-x64")
    if ($locked) { $restoreArgs += $locked }
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "Falló restore de la solución de pruebas." }

    $keygenRestore = @("restore", "Ticketfy.Keygen/Ticketfy.Keygen.csproj")
    if ($locked) { $keygenRestore += $locked }
    & dotnet @keygenRestore
    if ($LASTEXITCODE -ne 0) { throw "Falló restore de Keygen." }

    dotnet format Ticketfy.sln --verify-no-changes --no-restore --severity error
    if ($LASTEXITCODE -ne 0) { throw "Falló la verificación de formato." }
    dotnet format Ticketfy.Keygen/Ticketfy.Keygen.csproj --verify-no-changes --no-restore --severity error
    if ($LASTEXITCODE -ne 0) { throw "Falló la verificación de formato de Keygen." }

    dotnet build Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj --configuration Release --no-restore -p:RuntimeIdentifier=win-x64
    if ($LASTEXITCODE -ne 0) { throw "Falló el build Release." }
    dotnet build Ticketfy.Keygen/Ticketfy.Keygen.csproj --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Falló el build Release de Keygen." }

    $results = Join-Path $repoRoot "artifacts/TestResults"
    dotnet test Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj `
        --configuration Release --no-build --no-restore `
        -p:RuntimeIdentifier=win-x64 `
        --settings coverage.runsettings `
        --collect:"XPlat Code Coverage" `
        --results-directory $results `
        --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) { throw "Falló la suite automatizada." }

    $coverage = Get-ChildItem -LiteralPath $results -Filter "coverage.cobertura.xml" -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $coverage) { throw "No se generó coverage.cobertura.xml." }
    & (Join-Path $PSScriptRoot "assert-critical-coverage.ps1") -CoverageFile $coverage.FullName -MinimumPercent 75

    $auditProjects = @(
        "Ticketfy.Desktop/Ticketfy.Desktop.csproj",
        "Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj",
        "Ticketfy.Keygen/Ticketfy.Keygen.csproj"
    )
    foreach ($auditProject in $auditProjects) {
        $auditOutput = (& dotnet list $auditProject package --vulnerable --include-transitive 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) { throw "Falló la auditoría NuGet de $auditProject.`n$auditOutput" }
        Write-Host $auditOutput
        if ($auditOutput -match "has the following vulnerable packages|tiene los siguientes paquetes vulnerables") {
            throw "Se detectaron dependencias vulnerables en $auditProject."
        }
    }

    $first = & (Join-Path $PSScriptRoot "package-ci.ps1") -OutputDirectory "artifacts/package-a" -NoRestore
    if ($VerifyReproducible) {
        $second = & (Join-Path $PSScriptRoot "package-ci.ps1") -OutputDirectory "artifacts/package-b" -NoRestore
        if ($first.Sha256 -ne $second.Sha256) {
            throw "El paquete no es reproducible: $($first.Sha256) != $($second.Sha256)."
        }
        Write-Host "Reproducibilidad confirmada: $($first.Sha256)"
    }

    if ($RequireClean -and (git status --porcelain)) {
        throw "La canalización modificó archivos rastreados del checkout."
    }
}
finally {
    Pop-Location
}
