; Optional per-user installer for the Pairnets client (built by release.yml with Inno Setup 6).
; No admin rights needed; autostart stays a user setting inside Pairnets.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\client"
#endif

[Setup]
AppId={{5DB04D62-50C8-4967-B220-449C44AC1BA3}
AppName=Pairnets
AppVersion={#AppVersion}
AppPublisher=Pairnets
DefaultDirName={localappdata}\Programs\Pairnets
DefaultGroupName=Pairnets
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=PairnetsSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Pairnets.exe
CloseApplications=yes
; Tell Explorer to reload icons after an update, so the taskbar and Start menu show the current logo.
ChangesAssociations=yes

[Files]
Source: "{#SourceDir}\Pairnets.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README-client.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{userprograms}\Pairnets"; Filename: "{app}\Pairnets.exe"

[Registry]
; The nest's website opens pairnets:// after approving this computer, to bring Pairnets back to the
; front. The link carries nothing: the app fetches its key through its own secret poll.
Root: HKCU; Subkey: "Software\Classes\pairnets"; ValueType: string; ValueData: "URL:Pairnets"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\pairnets"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\pairnets\DefaultIcon"; ValueType: string; ValueData: "{app}\Pairnets.exe,0"
Root: HKCU; Subkey: "Software\Classes\pairnets\shell\open\command"; ValueType: string; ValueData: """{app}\Pairnets.exe"" ""%1"""

[Run]
; Windows 10/11 keep icons in a cache keyed by the exe path; refresh it so a replaced Pairnets.exe shows its new icon.
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden nowait skipifdoesntexist
Filename: "{app}\Pairnets.exe"; Description: "Start Pairnets now"; Flags: nowait postinstall skipifsilent
; After an update from inside Pairnets (a silent install) start it again by itself.
Filename: "{app}\Pairnets.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Pairnets.exe /F"; Flags: runhidden; RunOnceId: "StopPairnets"

[Code]
const
  // The installer's id from when Pairnets was called Tether.
  TetherUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E7A3C4B-2D7F-4E5A-9B1C-7F3E2A9D4C10}_is1';

// Removes the old Tether program (its uninstaller also stops it), so it does not run next to
// Pairnets. Its settings, sync state and logs stay: Pairnets takes them over when it first starts.
procedure RemoveTether();
var
  Uninstaller: String;
  ResultCode: Integer;
begin
  if RegQueryStringValue(HKCU, TetherUninstallKey, 'UninstallString', Uninstaller) then
  begin
    Uninstaller := RemoveQuotes(Uninstaller);
    if FileExists(Uninstaller) then
      Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemoveTether();
end;
