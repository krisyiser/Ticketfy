param([switch]$DevelopmentOnly)
if (-not $DevelopmentOnly) { throw "This script creates development certificates only. Commercial releases require a CA-issued code-signing certificate." }
$Subject = "CN=Ticketfy Local Development, O=Ticketfy, C=MX"
$Cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -KeyUsage DigitalSignature -FriendlyName "Valcore Code Signing Certificate" -NotAfter (Get-Date).AddYears(5) -CertStoreLocation "Cert:\CurrentUser\My"

$PlainSigningPassword = $env:TICKETFY_SIGNING_CERT_PASSWORD
if ([string]::IsNullOrWhiteSpace($PlainSigningPassword)) {
    throw "TICKETFY_SIGNING_CERT_PASSWORD must be configured before creating a certificate."
}
$Pwd = ConvertTo-SecureString -String $PlainSigningPassword -Force -AsPlainText
$PfxPath = if ($env:TICKETFY_DEV_CERT_OUTPUT) { $env:TICKETFY_DEV_CERT_OUTPUT } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Ticketfy\Secrets\TicketfyDevelopmentCodeSigning.pfx" }
$PfxDirectory = Split-Path -Parent $PfxPath
New-Item -ItemType Directory -Path $PfxDirectory -Force | Out-Null

Export-PfxCertificate -Cert $Cert -FilePath $PfxPath -Password $Pwd -Force
Write-Host "Development-only certificate exported outside the source tree: $PfxPath" -ForegroundColor Yellow
