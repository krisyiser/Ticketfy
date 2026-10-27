param (
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ProjectPath = Join-Path $PSScriptRoot "Ticketfy.Desktop.csproj"
$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$ProjectXml = Get-Content $ProjectPath
$ProjectVersion = [string](($ProjectXml.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version)
$TargetFramework = [string](($ProjectXml.Project.PropertyGroup | Where-Object TargetFramework | Select-Object -First 1).TargetFramework)
if ([string]::IsNullOrWhiteSpace($TargetFramework)) { throw "TargetFramework is required." }
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$ProjectXml = Get-Content $ProjectPath
    $Version = [string](($ProjectXml.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version)
    $Version = $Version.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use the numeric major.minor.patch format."
}
if ($Version -ne $ProjectVersion.Trim()) { throw 'Actualice primero la versión del proyecto; el release no admite versiones divergentes.' }
$ExpectedSdk = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$ActualSdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $ActualSdk -ne $ExpectedSdk) { throw "Se requiere el SDK fijado: $ExpectedSdk." }
if (git -C $RepositoryRoot status --porcelain) { throw 'El release firmado requiere un commit limpio y previamente aprobado por CI.' }
$ReleaseCommit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'No se pudo identificar el commit de release.' }
$ReleaseDir = Join-Path $RepositoryRoot "artifacts\signed-releases\$Version-$([Guid]::NewGuid().ToString('N'))"

$SigningThumbprint = $env:TICKETFY_SIGNING_CERT_THUMBPRINT
$TimestampUrl = $env:TICKETFY_TIMESTAMP_URL
if ([string]::IsNullOrWhiteSpace($SigningThumbprint)) { throw "TICKETFY_SIGNING_CERT_THUMBPRINT is required for every release." }
if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { throw "TICKETFY_TIMESTAMP_URL is required for a trusted release timestamp." }
$SignTool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
if ([string]::IsNullOrWhiteSpace($SignTool)) {
    $SignTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($SignTool)) { throw "signtool.exe was not found. Install the Windows SDK." }
New-Item -ItemType Directory -Path $ReleaseDir | Out-Null

function Invoke-RequiredCodeSigning([string]$Path) {
    & $SignTool sign /sha1 $SigningThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed: $Path" }
    & $SignTool verify /pa /all $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $Path" }
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Compilando Ticketfy v$Version Multi-Arquitectura (x64 / x86)..." -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Publicar versión 64-bit (win-x64)
Write-Host "[1/2] Publicando versión 64-bit (win-x64)..." -ForegroundColor Yellow
$PublishDir64 = Join-Path $ReleaseDir 'publish-x64'
dotnet publish $ProjectPath -c Release -r win-x64 --self-contained --output $PublishDir64 -p:RestoreLockedMode=true -p:Version=$Version -p:AssemblyVersion=$Version.0 -p:FileVersion=$Version.0

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error en la compilación x64." -ForegroundColor Red
    exit $LASTEXITCODE
}

Invoke-RequiredCodeSigning (Join-Path $PublishDir64 "Ticketfy.Desktop.exe")

# 2. Publicar versión 32-bit (win-x86)
Write-Host "[2/2] Publicando versión 32-bit (win-x86)..." -ForegroundColor Yellow
$PublishDir86 = Join-Path $ReleaseDir 'publish-x86'
dotnet publish $ProjectPath -c Release -r win-x86 --self-contained --output $PublishDir86 -p:RestoreLockedMode=true -p:Version=$Version -p:AssemblyVersion=$Version.0 -p:FileVersion=$Version.0

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error en la compilación x86." -ForegroundColor Red
    exit $LASTEXITCODE
}
Invoke-RequiredCodeSigning (Join-Path $PublishDir86 "Ticketfy.Desktop.exe")

# Each invocation owns a new output directory. Never stop an operating POS,
# overwrite a previous release, or clean paths relative to the caller's CWD.

# Validar versión del binario compilado
$BuiltVersion = (Get-Item "$PublishDir64\Ticketfy.Desktop.exe").VersionInfo.FileVersion
Write-Host "Verificando versión de ensamblado compilado: $BuiltVersion (Esperado: $Version.0)" -ForegroundColor Cyan
if ($BuiltVersion -ne "$Version.0") { throw 'La versión del ejecutable no coincide con el proyecto.' }

# 4. Empaquetado Velopack en directorios temporales aislados
Write-Host "Empaquetando con Velopack en entorno aislado (x64 y x86)..." -ForegroundColor Cyan
$TempPack64 = Join-Path $ReleaseDir "temp_x64_$Version"
$TempPack86 = Join-Path $ReleaseDir "temp_x86_$Version"

New-Item -ItemType Directory -Path $TempPack64 | Out-Null
New-Item -ItemType Directory -Path $TempPack86 | Out-Null

vpk pack -u Ticketfy.Desktop -v $Version -p $PublishDir64 -o $TempPack64 -e Ticketfy.Desktop.exe --packTitle "Ticketfy! (64-bit)"
if ($LASTEXITCODE -ne 0 -or !(Test-Path "$TempPack64\Ticketfy.Desktop-win-Setup.exe")) {
    Write-Host "ERROR CRÍTICO: Velopack pack x64 falló en el entorno aislado. Abortando." -ForegroundColor Red
    exit 1
}

vpk pack -u Ticketfy.Desktop -v $Version -p $PublishDir86 -o $TempPack86 -e Ticketfy.Desktop.exe --packTitle "Ticketfy! (32-bit)"
if ($LASTEXITCODE -ne 0 -or !(Test-Path "$TempPack86\Ticketfy.Desktop-win-Setup.exe")) {
    Write-Host "ERROR CRÍTICO: Velopack pack x86 falló en el entorno aislado. Abortando." -ForegroundColor Red
    exit 1
}

# Copiar artefactos verificados a los directorios finales de release
New-Item -ItemType Directory -Path "$ReleaseDir\x64" -Force | Out-Null
New-Item -ItemType Directory -Path "$ReleaseDir\x86" -Force | Out-Null

Copy-Item -Path "$TempPack64\*" -Destination "$ReleaseDir\x64\" -Recurse -Force
Copy-Item -Path "$TempPack86\*" -Destination "$ReleaseDir\x86\" -Recurse -Force

Write-Host "Compilando instalador nativo industrial de alta compatibilidad con Inno Setup (ISCC.exe)..." -ForegroundColor Cyan
$IsccPaths = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$IsccExe = $null
foreach ($p in $IsccPaths) {
    if (Test-Path $p) {
        $IsccExe = $p
        break
    }
}

if ($IsccExe) {
    $issFile = Join-Path $PSScriptRoot "ticketfy_setup.iss"
    if (Test-Path $issFile) {
        & $IsccExe "/DMyAppVersion=$Version" "/DMySourceDir=$PublishDir64" "/DMyOutputDir=$ReleaseDir" $issFile
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath "$ReleaseDir\Ticketfy-Setup-v$Version-x64.exe")) {
            throw 'No se generó el instalador Inno Setup. Se conserva la evidencia para diagnóstico.'
        }
        if ($LASTEXITCODE -eq 0 -and (Test-Path "$ReleaseDir\Ticketfy-Setup-v$Version-x64.exe")) {
            Copy-Item -Path "$ReleaseDir\Ticketfy-Setup-v$Version-x64.exe" -Destination "$ReleaseDir\Ticketfy-Setup-v$Version.exe" -Force
            
            Write-Host "¡Instalador Inno Setup v$Version compilado y verificado exitosamente!" -ForegroundColor Green
        }
    }
} else {
    Write-Host "Inno Setup no encontrado, usando fallback Velopack." -ForegroundColor Yellow
    Copy-Item -Path "$TempPack64\Ticketfy.Desktop-win-Setup.exe" -Destination "$ReleaseDir\Ticketfy-Setup-v$Version-x64.exe" -Force
    Copy-Item -Path "$TempPack64\Ticketfy.Desktop-win-Setup.exe" -Destination "$ReleaseDir\Ticketfy-Setup-v$Version.exe" -Force
}

if (Test-Path "$TempPack86\Ticketfy.Desktop-win-Setup.exe") {
    Copy-Item -Path "$TempPack86\Ticketfy.Desktop-win-Setup.exe" -Destination "$ReleaseDir\Ticketfy-Setup-v$Version-x86.exe" -Force
}
if (Test-Path "$TempPack64\Ticketfy.Desktop-win-Portable.zip") {
    Copy-Item -Path "$TempPack64\Ticketfy.Desktop-win-Portable.zip" -Destination "$ReleaseDir\Ticketfy-Portable-v$Version-x64.zip" -Force
}

# Every executable that leaves the build pipeline must carry and verify a
# trusted Authenticode signature. Unsigned output aborts the release.
Get-ChildItem -LiteralPath $ReleaseDir -Filter *.exe -Recurse | ForEach-Object {
    Invoke-RequiredCodeSigning $_.FullName
}

$ZipSetupPath = "$ReleaseDir\Ticketfy-Instalador-v$Version-x64.zip"
Compress-Archive -LiteralPath "$ReleaseDir\Ticketfy-Setup-v$Version-x64.exe" -DestinationPath $ZipSetupPath

# 4. Generar manifiesto local para trazabilidad de la compilación
Write-Host "Generando manifiesto local de compilación..." -ForegroundColor Cyan
$ArtifactHashes = @(Get-ChildItem -LiteralPath $ReleaseDir -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ file = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; bytes = $_.Length }
})
if (git -C $RepositoryRoot status --porcelain) { throw 'La compilación alteró fuentes rastreadas. No publique el resultado.' }
$LocalBuildJson = [ordered]@{
    version = $Version
    commit = $ReleaseCommit
    source_state = 'clean'
    sdk = $ActualSdk
    created_at = [DateTimeOffset]::UtcNow.ToString('O')
    mode = 'local-signed-candidate'
    qualification = 'Requiere aceptación de instalación, actualización, recuperación y piloto; la firma no acredita operación empresarial.'
    artifacts = $ArtifactHashes
} | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText("$ReleaseDir\local-build.json", $LocalBuildJson, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "¡Construcción local completada con éxito! No se publicó ningún artefacto remoto." -ForegroundColor Green
Write-Host "Instalador 64-bit: $ReleaseDir\Ticketfy-Setup-v$Version-x64.exe" -ForegroundColor Green
Write-Host "Instalador 32-bit: $ReleaseDir\Ticketfy-Setup-v$Version-x86.exe" -ForegroundColor Green
Write-Host "Manifiesto local:  $ReleaseDir\local-build.json" -ForegroundColor Green

# Keep intermediate artifacts for diagnostics and independently qualified installation tests.
Write-Host "Proceso finalizado con éxito." -ForegroundColor Green
