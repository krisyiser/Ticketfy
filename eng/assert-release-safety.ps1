[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot '../Ticketfy.Desktop/build_release.ps1'
$parseTokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    [IO.Path]::GetFullPath($scriptPath), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Sintaxis inválida del release: $($parseErrors | Out-String)" }
$destructiveCommands = $ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -in @('Stop-Process', 'Remove-Item', 'rm', 'rmdir', 'del', 'taskkill', 'kill')
}, $true)
if ($destructiveCommands.Count) {
    throw 'El empaquetado no debe detener procesos ni eliminar archivos. Use un directorio de salida nuevo por ejecución.'
}
Write-Host 'Script de release: sintaxis y prohibición de borrado/parada verificadas (no se ejecuta el instalador).'
