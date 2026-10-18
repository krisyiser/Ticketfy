[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CoverageFile,
    [ValidateRange(0, 100)]
    [double]$MinimumPercent = 75,
    [ValidateRange(0, 100)]
    [double]$MinimumFinancialServicePercent = 70
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
    "Services/Implementations/ShiftLedgerValidator.cs",
    "Services/Implementations/PurchaseService.cs",
    "Services/Implementations/ExpenseService.cs",
    "Services/Implementations/GiftcardService.cs",
    "Services/Implementations/PromotionService.cs",
    "Services/Implementations/SupplierService.cs",
    "Services/Implementations/ItemKitService.cs",
    "Services/Implementations/CategoryService.cs",
    "Services/Implementations/CommercialLedger.cs",
    "Services/Implementations/CommandIdentity.cs",
    "Services/Implementations/CommercialReconciliationService.cs",
    "Services/Implementations/HistoricalReconciliationService.cs",
    "Services/Implementations/IncomingTransferService.cs",
    "Services/Implementations/PerformanceAnalyticsService.cs",
    "Services/CommercialOperationContext.cs",
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

# Do not let well-covered services hide an untested financial command service.
# Initialization includes platform/startup paths and keeps the aggregate requirement.
$belowServiceMinimum = @($rows | Where-Object {
    $_.File -ne "DatabaseInitializer.cs" -and $_.Percent -lt $MinimumFinancialServicePercent
})
if ($belowServiceMinimum.Count -gt 0) {
    throw "Servicios por debajo del mínimo individual $MinimumFinancialServicePercent%: $($belowServiceMinimum.File -join ', ')"
}
