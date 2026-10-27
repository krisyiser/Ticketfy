#ifndef MyAppVersion
  #error MyAppVersion is required
#endif
#ifndef MySourceDir
  #error MySourceDir is required
#endif
#ifndef MyOutputDir
  #error MyOutputDir is required
#endif
#ifndef MyCommit
  #error MyCommit is required
#endif
#if VER < EncodeVer(6, 4, 0)
  #error Inno Setup 6.4 or newer is required
#endif

[Setup]
; Separate identity: this never upgrades or uninstalls the commercial edition.
AppId={{9343122D-165F-4A27-8B70-11806632AB51}
AppName=Ticketfy - Pruebas internas
AppVersion={#MyAppVersion}
AppVerName=Ticketfy {#MyAppVersion} - Pruebas internas ({#MyCommit})
AppPublisher=Valcore
DefaultDirName={localappdata}\Programs\Ticketfy.InternalTesting
DefaultGroupName=Ticketfy - Pruebas internas
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#MyOutputDir}
OutputBaseFilename=Ticketfy-Pruebas-{#MyAppVersion}-{#MyCommit}-win-x64-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Ticketfy.Desktop.exe
UninstallDisplayName=Ticketfy - Pruebas internas
AppMutex=Ticketfy.InternalTesting
SetupMutex=Ticketfy.InternalTesting.Setup
CloseApplications=no
RestartApplications=no
InfoBeforeFile={#MySourceDir}\LEEME-PRUEBAS.txt
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription=Ticketfy - Candidato de pruebas internas sin firma
VersionInfoCompany=Valcore

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo a Ticketfy - Pruebas internas"; GroupDescription: "Accesos directos:"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Ticketfy - Pruebas internas"; Filename: "{app}\Ticketfy.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Ticketfy - Pruebas internas"; Filename: "{app}\Ticketfy.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\Instrucciones de pruebas"; Filename: "{app}\LEEME-PRUEBAS.txt"

[Run]
Filename: "{app}\Ticketfy.Desktop.exe"; Description: "Abrir Ticketfy - Pruebas internas"; Flags: nowait postinstall skipifsilent unchecked

; Deliberately no UninstallDelete: databases, keys and backups are outside {app} and must survive uninstall.
