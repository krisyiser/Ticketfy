[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CoverageFile,
    [ValidateRange(0, 100)]
    [double]$MinimumPercent = 75
)

$ErrorActionPreference = "Stop"
$resolvedCoverage = (Resolve-Path -LiteralPath $CoverageFile).Path
[xml]$coverage = Get-Content -LiteralPath $resolvedCoverage

$criticalFiles = @(
    "Services/Implementations/SaleService.cs",
    "Services/Implementations/SaleService.Cancellation.cs",
    "Services/Implementations/SaleService.Inventory.cs",
    "Services/Implementations/SaleService.Recovery.cs",
    "Services/Implementations/ProductService.cs",
    "Services/Implementations/CustomerService.cs",
    "Services/Implementations/ShiftService.cs",
    "Services/Implementations/BackupService.cs",
    "Services/Implementations/FiscalIntegrationQueueService.cs",
    "Services/Implementations/MercadoPagoTerminalService.cs",
    "Core/Startup/DatabaseInitializer.cs"
)

$classes = @($coverage.coverage.packages.package.classes.class | Where-Object {
    $normalized = $_.filename.Replace('\', '/')
    @($criticalFiles | Where-Object {
        $normalized.EndsWith($_, [StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
})

$missing = @($criticalFiles | Where-Object {
    $expected = $_
    -not ($classes | Where-Object {
        $_.filename.Replace('\', '/').EndsWith($expected, [StringComparison]::OrdinalIgnoreCase)
    })
})
if ($missing.Count -gt 0) {
    throw "El reporte no contiene todos los archivos críticos: $($missing -join ', ')"
}

$rows = foreach ($group in ($classes | Group-Object filename)) {
    $valid = 0
    $covered = 0
    foreach ($class in $group.Group) {
        foreach ($line in @($class.lines.line)) {
            $valid++
            if ([int]$line.hits -gt 0) { $covered++ }
        }
    }
    [pscustomobject]@{
        File = [IO.Path]::GetFileName($group.Name)
        Covered = $covered
        Valid = $valid
        Percent = if ($valid -eq 0) { 0 } else { [Math]::Round(100 * $covered / $valid, 2) }
    }
}

$totalValid = ($rows | Measure-Object Valid -Sum).Sum
$totalCovered = ($rows | Measure-Object Covered -Sum).Sum
if ($totalValid -eq 0) { throw "El reporte de cobertura crítica no contiene líneas medibles." }
$criticalPercent = [Math]::Round(100 * $totalCovered / $totalValid, 2)

$rows | Sort-Object File | Format-Table -AutoSize
Write-Host "Cobertura crítica agregada: $criticalPercent% ($totalCovered/$totalValid líneas; mínimo $MinimumPercent%)."

if ($criticalPercent -lt $MinimumPercent) {
    throw "La cobertura crítica $criticalPercent% está por debajo del mínimo $MinimumPercent%."
}
