param (
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ProjectPath = Join-Path $PSScriptRoot "Ticketfy.Desktop.csproj"
$ReleaseDir = Join-Path $PSScriptRoot "Output\Releases"
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$ProjectXml = Get-Content $ProjectPath
    $Version = [string](($ProjectXml.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version)
    $Version = $Version.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use the numeric major.minor.patch format."
}

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
$PublishDir64 = Join-Path $PSScriptRoot "bin\Release\net9.0\win-x64\publish"
dotnet publish $ProjectPath -c Release -r win-x64 --self-contained -p:Version=$Version -p:AssemblyVersion=$Version.0 -p:FileVersion=$Version.0 /p:EnableSourceLink=false /p:EnableSourceControlManagerQueries=false /p:PublishRepositoryUrl=false

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error en la compilación x64." -ForegroundColor Red
    exit $LASTEXITCODE
}

Invoke-RequiredCodeSigning (Join-Path $PublishDir64 "Ticketfy.Desktop.exe")

# 2. Publicar versión 32-bit (win-x86)
Write-Host "[2/2] Publicando versión 32-bit (win-x86)..." -ForegroundColor Yellow
$PublishDir86 = Join-Path $PSScriptRoot "bin\Release\net9.0\win-x86\publish"
dotnet publish $ProjectPath -c Release -r win-x86 --self-contained -p:Version=$Version -p:AssemblyVersion=$Version.0 -p:FileVersion=$Version.0 /p:EnableSourceLink=false /p:EnableSourceControlManagerQueries=false /p:PublishRepositoryUrl=false

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error en la compilación x86." -ForegroundColor Red
    exit $LASTEXITCODE
}
Invoke-RequiredCodeSigning (Join-Path $PublishDir86 "Ticketfy.Desktop.exe")

# 3. Purga estricta de instaladores antiguos para garantizar congruencia absoluta de versión
Write-Host "Deteniendo procesos que puedan bloquear los archivos de release..." -ForegroundColor Cyan
Get-Process -Name "Ticketfy.Desktop", "vpk" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Host "Limpiando ejecutables e instaladores previos en Output\Releases..." -ForegroundColor Cyan
Remove-Item -Path "$ReleaseDir\*.exe" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$ReleaseDir\x64\*.exe" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$ReleaseDir\x86\*.exe" -Force -ErrorAction SilentlyContinue

# Validar versión del binario compilado
$BuiltVersion = (Get-Item "$PublishDir64\Ticketfy.Desktop.exe").VersionInfo.FileVersion
Write-Host "Verificando versión de ensamblado compilado: $BuiltVersion (Esperado: $Version.0)" -ForegroundColor Cyan

# 4. Empaquetado Velopack en directorios temporales aislados
Write-Host "Empaquetando con Velopack en entorno aislado (x64 y x86)..." -ForegroundColor Cyan
$TempPack64 = Join-Path $ReleaseDir "temp_x64_$Version"
$TempPack86 = Join-Path $ReleaseDir "temp_x86_$Version"

Remove-Item -Recurse -Force $TempPack64 -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $TempPack86 -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $TempPack64 -Force | Out-Null
New-Item -ItemType Directory -Path $TempPack86 -Force | Out-Null

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
    "C:\Users\YERSI\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
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
        $issContent = Get-Content $issFile -Raw
        $issContent = $issContent -replace '#define MyAppVersion "[^"]+"', "#define MyAppVersion `"$Version`""
        $issContent = $issContent -replace 'OutputBaseFilename=Ticketfy-Setup-v[^\r\n]+', "OutputBaseFilename=Ticketfy-Setup-v$Version-x64"
        Set-Content -Path $issFile -Value $issContent -Encoding UTF8

        & $IsccExe $issFile
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
if (Test-Path $ZipSetupPath) { Remove-Item -LiteralPath $ZipSetupPath -Force }
Compress-Archive -LiteralPath "$ReleaseDir\Ticketfy-Setup-v$Version-x64.exe" -DestinationPath $ZipSetupPath -Force

Remove-Item -Recurse -Force $TempPack64 -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $TempPack86 -ErrorAction SilentlyContinue

# 4. Generar manifiesto local para trazabilidad de la compilación
Write-Host "Generando manifiesto local de compilación..." -ForegroundColor Cyan
$LocalBuildJson = @"
{
  "version": "$Version",
  "created_at": "$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK')",
  "mode": "local-development",
  "artifacts": {
    "x64": "Ticketfy-Setup-v$Version-x64.exe",
    "x86": "Ticketfy-Setup-v$Version-x86.exe",
    "portable": "Ticketfy-Portable-v$Version-x64.zip"
  }
}
"@
[System.IO.File]::WriteAllText("$ReleaseDir\local-build.json", $LocalBuildJson, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "¡Construcción local completada con éxito! No se publicó ningún artefacto remoto." -ForegroundColor Green
Write-Host "Instalador 64-bit: $ReleaseDir\Ticketfy-Setup-v$Version-x64.exe" -ForegroundColor Green
Write-Host "Instalador 32-bit: $ReleaseDir\Ticketfy-Setup-v$Version-x86.exe" -ForegroundColor Green
Write-Host "Manifiesto local:  $ReleaseDir\local-build.json" -ForegroundColor Green

# 5. Limpieza de temporales
Write-Host "Limpiando artefactos temporales..." -ForegroundColor Cyan
Remove-Item -Recurse -Force ".\bin" -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force ".\obj" -ErrorAction SilentlyContinue
Write-Host "Proceso finalizado con éxito." -ForegroundColor Green
