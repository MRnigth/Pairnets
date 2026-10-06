; Optional per-user installer for the Tether client (built by release.yml with Inno Setup 6).
; No admin rights needed; autostart stays a user setting inside Tether.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\client"
#endif

[Setup]
AppId={{6E7A3C4B-2D7F-4E5A-9B1C-7F3E2A9D4C10}
AppName=Tether
AppVersion={#AppVersion}
AppPublisher=Tether
DefaultDirName={localappdata}\Programs\Tether
DefaultGroupName=Tether
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=TetherSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Tether.exe
CloseApplications=yes
; Tell Explorer to reload icons after an update, so the taskbar and Start menu show the current logo.
ChangesAssociations=yes

[Files]
Source: "{#SourceDir}\Tether.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README-client.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{userprograms}\Tether"; Filename: "{app}\Tether.exe"

[Run]
; Windows 10/11 keep icons in a cache keyed by the exe path; refresh it so a replaced Tether.exe shows its new icon.
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden nowait skipifdoesntexist
Filename: "{app}\Tether.exe"; Description: "Start Tether now"; Flags: nowait postinstall skipifsilent
; After an update from inside Tether (a silent install) start it again by itself.
Filename: "{app}\Tether.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Tether.exe /F"; Flags: runhidden; RunOnceId: "StopTether"
