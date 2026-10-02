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

[Files]
Source: "{#SourceDir}\Tether.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README-client.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{userprograms}\Tether"; Filename: "{app}\Tether.exe"

[Run]
Filename: "{app}\Tether.exe"; Description: "Start Tether now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Tether.exe /F"; Flags: runhidden; RunOnceId: "StopTether"
