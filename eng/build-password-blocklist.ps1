[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceFile,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
# Public SecLists/NCSC snapshot, never user credentials. Fetching is a separate maintainer action.
$sourceHash = 'C2E5696882C603B76BB67A47EE970897E5A76FC4C3F5547ABE3D0CA340C576E0'
if ((Get-FileHash -LiteralPath $SourceFile -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'La fuente no coincide con la referencia pública revisada.'
}
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$secrets = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $SourceFile).Path, $utf8)
$hashes = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
foreach ($value in $secrets) {
    if ([string]::IsNullOrWhiteSpace($value)) { continue }
    # Screening only: account hashing continues to use the literal secret.
    $normalized = $value.Trim().Normalize([Text.NormalizationForm]::FormC).ToUpperInvariant()
    $digest = [Security.Cryptography.SHA256]::HashData($utf8.GetBytes($normalized))
    $null = $hashes.Add([Convert]::ToHexString($digest))
}
if (-not (Test-Path -LiteralPath $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory | Out-Null }
$output = Join-Path $OutputDirectory 'common-passwords-v2.sha256'
[IO.File]::WriteAllText($output, ([string]::Join("`n", $hashes) + "`n"), $utf8)
[pscustomobject]@{ Count = $hashes.Count; Sha256 = (Get-FileHash -LiteralPath $output).Hash; Bytes = (Get-Item -LiteralPath $output).Length }
