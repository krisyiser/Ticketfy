#define MyAppName "TICKETFY!"
#ifndef MyAppVersion
  #error MyAppVersion must be passed by build_release.ps1
#endif
#define MyAppPublisher "Valcore"
#define MyAppURL "https://valcore.cloud"
#define MyAppExeName "Ticketfy.Desktop.exe"
#ifndef MySourceDir
  #error MySourceDir must be passed by build_release.ps1
#endif
#ifndef MyOutputDir
  #error MyOutputDir must be passed by build_release.ps1
#endif

[Setup]
AppId={{D8F42F99-4A9B-4C38-9B7E-7F891D817E23}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={userappdata}\Ticketfy.Desktop\current
DisableDirPage=yes
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=Ticketfy-Setup-v{#MyAppVersion}-x64
OutputDir={#MyOutputDir}
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

; Metadatos Empresariales de Firma e Identidad Windows PE - VALCORE
VersionInfoCompany=Valcore
VersionInfoDescription=Valcore TICKETFY! Punto de Venta
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCopyright=Copyright © 2026 Valcore. Todos los derechos reservados.
VersionInfoProductName=Valcore TICKETFY! Punto de Venta
VersionInfoProductVersion={#MyAppVersion}.0
VersionInfoOriginalFileName=Ticketfy-Setup-v{#MyAppVersion}-x64.exe

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent













