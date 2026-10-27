[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$results = (Resolve-Path -LiteralPath $ResultsDirectory).Path
$executable = Join-Path $package 'Ticketfy.Desktop.exe'
$laboratory = Join-Path $results ('startup-' + [Guid]::NewGuid().ToString('N'))
# The executable also rejects an existing data directory before opening any database.
$process = Start-Process -FilePath $executable -ArgumentList @('--verify-startup', '--data-directory', ('"' + $laboratory + '"')) -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(60000)) {
        # Only the child created above can be terminated; never search for or stop a running POS.
        $process.Kill()
        throw 'La verificación del ejecutable excedió 60 segundos. Conserve el laboratorio para diagnóstico.'
    }
    if ($process.ExitCode -ne 0) { throw "El ejecutable falló al inicializar el laboratorio. Código: $($process.ExitCode)." }
    $report = Get-Content -LiteralPath (Join-Path $laboratory 'startup-verification.json') -Raw | ConvertFrom-Json
    if (-not $report.Succeeded -or $report.BootstrapPasses -ne 2 -or -not $report.BackupRestorationVerified -or $report.FacturamaEnabled) {
        throw 'La evidencia de arranque del candidato está incompleta.'
    }
    if ($report.PasswordPolicyVersion -lt 2 -or $report.PasswordBlocklistEntries -le 0 -or
        $report.PasswordBlocklistSha256 -notmatch '^[A-F0-9]{64}$' -or
        -not (Test-Path -LiteralPath (Join-Path $package 'ThirdPartyNotices/SecLists-LICENSE.txt') -PathType Leaf)) {
        throw 'El paquete no contiene evidencia de la política de contraseñas y sus avisos de licencia.'
    }
    $evidence = [ordered]@{
        Succeeded = $true
        ExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        AssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $package 'Ticketfy.Desktop.dll') -Algorithm SHA256).Hash
        ReportPath = Join-Path $laboratory 'startup-verification.json'
    }
    [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $package) 'startup-verified.json'), ($evidence | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host "Arranque empaquetado verificado: $laboratory (dos aperturas, escritura, reapertura y restauración)."
}
finally { $process.Dispose() }
