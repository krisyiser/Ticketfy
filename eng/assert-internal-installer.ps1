[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$InstallerDirectory)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$package = (Resolve-Path -LiteralPath $InstallerDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $package 'installer-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.purpose -ne 'internal-testing-installer' -or [IO.Path]::GetFileName($manifest.installer) -ne $manifest.installer) {
    throw 'No es un instalador interno reconocido.'
}
$installer = Join-Path $package $manifest.installer
if ((Get-FileHash -LiteralPath $installer).Hash -ne $manifest.sha256) { throw 'El instalador cambió desde su compilación.' }
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9343122D-165F-4A27-8B70-11806632AB51}_is1'
$registration32 = 'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{9343122D-165F-4A27-8B70-11806632AB51}_is1'
if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $registration32)) {
    throw 'Ya existe una edición interna instalada para este usuario. No se reemplazará para ejecutar la prueba.'
}
$group = Join-Path ([Environment]::GetFolderPath('Programs')) 'Ticketfy - Pruebas internas'
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Ticketfy - Pruebas internas.lnk'
if ((Test-Path -LiteralPath $group) -or (Test-Path -LiteralPath $desktopLink)) {
    throw 'Existen accesos directos de pruebas anteriores. Se conservan sin modificarlos.'
}
$id = [Guid]::NewGuid().ToString('N')
$lab = Join-Path $repo "artifacts/installer-checks/$id"
$installed = Join-Path $lab 'Installed'
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Ticketfy.InternalTesting/InstallerChecks/$id"
foreach ($path in @($lab, $data)) {
    if (Test-Path -LiteralPath $path) { throw 'El laboratorio debe ser nuevo.' }
    for ($ancestor = [IO.DirectoryInfo]::new($path); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'El laboratorio no admite rutas redirigidas.' }
    }
}
New-Item -ItemType Directory -Path $lab | Out-Null
function Invoke-OwnedProcess([string]$Executable, [string[]]$Arguments) {
    $child = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (-not $child.WaitForExit(60000)) { $child.Kill(); $child.WaitForExit(); throw 'El proceso propio excedió 60 segundos.' }
        return $child.ExitCode
    }
    finally { $child.Dispose() }
}
function Install-Test([string]$LogName) {
    Invoke-OwnedProcess $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=desktopicon',
        ('/DIR="' + $installed + '"'), ('/LOG="' + (Join-Path $lab $LogName) + '"'))
}
function Assert-InstalledPayload {
    foreach ($file in $manifest.payload) {
        $target = [IO.Path]::GetFullPath((Join-Path $installed $file.path))
        if (-not $target.StartsWith($installed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $target).Hash -ne $file.sha256) { throw 'El archivo instalado no coincide con el paquete.' }
    }
}
$normal = $null
$installedSuccessfully = $false
try {
    if ((Install-Test 'install.log') -ne 0) { throw 'Falló la instalación del candidato.' }
    $installedSuccessfully = $true
    Assert-InstalledPayload
    if (-not (Test-Path -LiteralPath $registration)) { throw 'Falta el registro de desinstalación para el usuario.' }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($link in @((Join-Path $group 'Ticketfy - Pruebas internas.lnk'), $desktopLink)) {
        $shortcut = $shell.CreateShortcut($link)
        if ($shortcut.TargetPath -ne (Join-Path $installed 'Ticketfy.Desktop.exe') -or $shortcut.Arguments) {
            throw 'El acceso directo no corresponde al ejecutable aislado.'
        }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut)
    }
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    # This starts only the just-installed executable, with fresh synthetic data and no dependency on a global SDK.
    $oldDotnetRoot = $env:DOTNET_ROOT
    $oldLookup = $env:DOTNET_MULTILEVEL_LOOKUP
    try {
        $env:DOTNET_ROOT = Join-Path $lab 'no-global-dotnet'
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        $normal = Start-Process -FilePath (Join-Path $installed 'Ticketfy.Desktop.exe') -ArgumentList @('--data-directory', ('"' + $data + '"')) -WindowStyle Hidden -PassThru
    }
    finally { $env:DOTNET_ROOT = $oldDotnetRoot; $env:DOTNET_MULTILEVEL_LOOKUP = $oldLookup }
    Start-Sleep -Seconds 20
    $normal.Refresh()
    if ($normal.HasExited) { throw 'El ejecutable instalado terminó prematuramente.' }
    $texts = @(Get-ChildItem -LiteralPath (Join-Path $data 'Logs') -Filter '*.log' -File | ForEach-Object {
        $stream = [IO.File]::Open($_.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $reader = [IO.StreamReader]::new($stream)
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    })
    $logText = $texts -join "`n"
    if (-not $logText.Contains('License validated successfully. Starting normal operation') -or $logText -match '\[(ERR|FTL)\]') {
        throw 'El arranque instalado no llegó a la operación normal sin errores.'
    }
    $windowCreated = $normal.MainWindowHandle -ne [IntPtr]::Zero
    if ($windowCreated -and $normal.MainWindowTitle -notlike '*PRUEBAS INTERNAS*') { throw 'Falta la identificación visible de pruebas.' }
    if ((Install-Test 'blocked-running-install.log') -eq 0) { throw 'El instalador aceptó actualizar una aplicación de pruebas abierta.' }
    $normal.Refresh()
    if ($normal.HasExited) { throw 'El instalador cerró una aplicación que no debía detener.' }
    Assert-InstalledPayload
    [void]$normal.CloseMainWindow()
    if (-not $normal.WaitForExit(10000)) { $normal.Kill(); $normal.WaitForExit() }
    $normal.Dispose(); $normal = $null
    $database = Join-Path $data 'Database/ticketfy.db'
    if (-not (Test-Path -LiteralPath $database)) { throw 'Falta la base sintética del proceso instalado.' }
    $before = @(Get-ChildItem -LiteralPath $data -File -Recurse | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName; Hash = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    if ((Install-Test 'reinstall.log') -ne 0) { throw 'Falló la reinstalación.' }
    Assert-InstalledPayload
    foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path).Hash -ne $file.Hash) { throw 'La reinstalación alteró los datos.' } }
    $uninstaller = [IO.Path]::GetFullPath((Join-Path $installed 'unins000.exe'))
    if (-not $uninstaller.StartsWith($lab + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Desinstalador fuera del laboratorio.' }
    if ((Invoke-OwnedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $lab 'uninstall.log') + '"'))) -ne 0) { throw 'Falló la desinstalación.' }
    foreach ($file in $before) { if ((Get-FileHash -LiteralPath $file.Path).Hash -ne $file.Hash) { throw 'La desinstalación alteró los datos.' } }
    if ((Test-Path -LiteralPath (Join-Path $installed 'Ticketfy.Desktop.exe')) -or (Test-Path -LiteralPath $registration) -or
        (Test-Path -LiteralPath $desktopLink) -or (Test-Path -LiteralPath $group)) { throw 'La desinstalación dejó programa, registro o accesos directos.' }
    $report = [ordered]@{
        succeeded = $true; commit = $manifest.commit; installer_sha256 = $manifest.sha256
        windows = [Environment]::OSVersion.Version.ToString(); os_architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        installed_payload_verified = $true; shortcuts_verified = $true; normal_startup_verified = $true; main_window_created = $windowCreated
        running_update_refused = $true; reinstall_preserved_data = $true; uninstall_preserved_data = $true
        user_registration_removed = $true; facturama_enabled = $false; operational_database_touched = $false
        manual_acceptance_completed = $false; checked_utc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'installer-verification.json') -Encoding utf8
    [pscustomobject]@{ Report = Join-Path $package 'installer-verification.json'; Laboratory = $lab; SyntheticData = $data; Succeeded = $true }
}
finally {
    if ($normal) { if (-not $normal.HasExited) { $normal.Kill(); $normal.WaitForExit() }; $normal.Dispose() }
    if ($installedSuccessfully -and (Test-Path -LiteralPath $registration)) {
        Write-Warning "La verificación no terminó de desinstalar el candidato. Se conserva únicamente la instalación de laboratorio: $installed"
    }
}
