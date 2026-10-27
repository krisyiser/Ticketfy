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
    $expectedSdk = (Get-Content (Join-Path $repoRoot "global.json") -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) {
        throw "Se requiere SDK $expectedSdk; SDK seleccionado: $actualSdk."
    }
    if ($RequireClean -and (git status --porcelain)) {
        throw "La canalización reproducible requiere un checkout limpio."
    }
    & (Join-Path $PSScriptRoot 'assert-release-safety.ps1')

    $locked = if ($LockedRestore) { "--locked-mode" } else { $null }
    $restoreArgs = @("restore", "Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj", "-p:RuntimeIdentifier=win-x64", "-p:Configuration=Release")
    if ($locked) { $restoreArgs += $locked }
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "Falló restore de la solución de pruebas." }

    $keygenRestore = @("restore", "Ticketfy.Keygen/Ticketfy.Keygen.csproj")
    if ($locked) { $keygenRestore += $locked }
    & dotnet @keygenRestore
    if ($LASTEXITCODE -ne 0) { throw "Falló restore de Keygen." }

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "Falló restore de las herramientas locales." }

    dotnet format Ticketfy.sln --verify-no-changes --no-restore --severity error
    if ($LASTEXITCODE -ne 0) { throw "Falló la verificación de formato." }
    dotnet format Ticketfy.Keygen/Ticketfy.Keygen.csproj --verify-no-changes --no-restore --severity error
    if ($LASTEXITCODE -ne 0) { throw "Falló la verificación de formato de Keygen." }

    dotnet build Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj --configuration Release --no-restore -p:RuntimeIdentifier=win-x64
    if ($LASTEXITCODE -ne 0) { throw "Falló el build Release." }
    dotnet build Ticketfy.Keygen/Ticketfy.Keygen.csproj --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Falló el build Release de Keygen." }

    foreach ($context in @("AppDbContext", "AuditDbContext")) {
        # Design-time factories use scratch paths, never the operational database.
        dotnet tool run dotnet-ef migrations has-pending-model-changes `
            --project Ticketfy.Desktop --context $context --configuration Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Modelo sin migración versionada: $context." }
    }

    $runDirectory = "artifacts/quality-$([Guid]::NewGuid().ToString('N'))"
    $results = Join-Path $repoRoot "$runDirectory/TestResults"
    dotnet test Ticketfy.Desktop.Tests/Ticketfy.Desktop.Tests.csproj `
        --configuration Release --no-build --no-restore `
        -p:RuntimeIdentifier=win-x64 `
        --settings coverage.runsettings `
        --collect:"XPlat Code Coverage" `
        --results-directory $results `
        --logger "console;verbosity=minimal" `
        --logger "trx;LogFileName=quality.trx"
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
        # .NET 10 package list restores implicitly unless disabled. Audit the exact
        # Release graph already tested, without replacing assets with a Debug graph.
        $auditOutput = (& dotnet package list --project $auditProject --no-restore --vulnerable --include-transitive --format json --output-version 1 | Out-String)
        if ($LASTEXITCODE -ne 0) { throw "Falló la auditoría NuGet de $auditProject.`n$auditOutput" }
        Write-Host $auditOutput
        $audit = $auditOutput | ConvertFrom-Json
        $auditFile = Join-Path $results "$([IO.Path]::GetFileNameWithoutExtension($auditProject))-dependencies.json"
        [IO.File]::WriteAllText($auditFile, $auditOutput, [Text.UTF8Encoding]::new($false))
        if ($audit.problems.Count -gt 0) { throw "La auditoría NuGet reportó problemas: $auditOutput" }
        $vulnerable = @($audit.projects.frameworks | ForEach-Object {
            @($_.topLevelPackages) + @($_.transitivePackages) |
                Where-Object { $_.vulnerabilities.Count -gt 0 }
        })
        if ($vulnerable.Count -gt 0) {
            throw "Se detectaron dependencias vulnerables en $auditProject."
        }
    }

    $first = & (Join-Path $PSScriptRoot "package-ci.ps1") -OutputDirectory "$runDirectory/package-a" -NoRestore
    & (Join-Path $PSScriptRoot "assert-packaged-startup.ps1") -PackageDirectory "$runDirectory/package-a/publish-win-x64" -ResultsDirectory $results
    if ($VerifyReproducible) {
        $second = & (Join-Path $PSScriptRoot "package-ci.ps1") -OutputDirectory "$runDirectory/package-b" -NoRestore
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
