; Installer for mi2o Gaming Center (Inno Setup 6 — https://jrsoftware.org/isinfo.php).
; Built by scripts\build-installer.ps1, which publishes the app first:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\publish\win-x64 installer\mi2oGamingCenter.iss
; Output: publish\installer\mi2oGamingCenter-Setup-<version>.exe

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

#define AppName "mi2o Gaming Center"
#define AppExe "mi2oGamingCenter.exe"

[Setup]
; Never change AppId: Windows uses it to recognise updates of the same program.
AppId={{5B8C2E61-3F4D-4C7A-9E1B-6D2A7F0C9B43}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=mi2o
DefaultDirName={autopf}\mi2o Gaming Center
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\GamingCenter.App\Resources\Images\app.ico
OutputDir=..\publish\installer
OutputBaseFilename=mi2oGamingCenter-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
; Closes a running copy before an update replaces its files.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,appsettings.json"
; Keep a shop's own settings (data folder, demo data) when updating.
Source: "{#SourceDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; The database, images, backups and license.key live in %LOCALAPPDATA%\mi2oGamingCenter
; and are NOT removed on uninstall, so reinstalling keeps all the shop's data.
