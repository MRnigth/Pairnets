; Optional per-user installer for the Pairnets client (built by release.yml with Inno Setup 6).
; No admin rights needed. The extras (Explorer, start-up, updates, where files live) are ticked on the Tasks page and
; stay settings inside Pairnets.
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
AppCopyright=(c) 2026 Pairnets
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
; An upgrade keeps the boxes ticked last time (and the app's settings: see LeaveChoicesForTheApp).
UsePreviousTasks=yes
; Tell Explorer to reload icons after an update, so the taskbar and Start menu show the current logo.
ChangesAssociations=yes

; The "Select Additional Tasks" page: what Pairnets may add to this PC. A silent update from inside Pairnets keeps the
; earlier choices and leaves the app's settings alone; an install by hand passes the choices on to the app (see [Code]).
[Tasks]
Name: "startmenu"; Description: "Add Pairnets to the Start menu"; GroupDescription: "Icons:"
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Icons:"; Flags: unchecked
Name: "explorerside"; Description: "Add Pairnets to File Explorer's left side (like OneDrive)"; GroupDescription: "File Explorer:"
Name: "explorermenu"; Description: "Add Pairnets to the right-click menu (only inside your Pairnets folder)"; GroupDescription: "File Explorer:"
Name: "syncticks"; Description: "Show sync ticks in File Explorer"; GroupDescription: "File Explorer:"
Name: "wherehere"; Description: "Everything on this PC (works without internet)"; GroupDescription: "Where your files live (each folder can be changed later):"; Flags: exclusive
Name: "whereondemand"; Description: "On-Demand: files show in Explorer and download when you open them"; GroupDescription: "Where your files live (each folder can be changed later):"; Flags: exclusive unchecked
Name: "wherenest"; Description: "Only on the nest: nothing on this PC, open files in Pairnets › Files"; GroupDescription: "Where your files live (each folder can be changed later):"; Flags: exclusive unchecked
Name: "checkupdates"; Description: "Look for new Pairnets versions by itself"; GroupDescription: "Updates and start-up:"
Name: "updatenest"; Description: "Update my nest by itself"; GroupDescription: "Updates and start-up:"; Flags: unchecked
Name: "autostart"; Description: "Start Pairnets when I log in"; GroupDescription: "Updates and start-up:"

[Files]
Source: "{#SourceDir}\Pairnets.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README-client.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{userprograms}\Pairnets"; Filename: "{app}\Pairnets.exe"; Tasks: startmenu
Name: "{userdesktop}\Pairnets"; Filename: "{app}\Pairnets.exe"; Tasks: desktopicon

[Registry]
; The nest's website opens pairnets:// after approving this computer, to bring Pairnets back to the
; front. The link carries nothing: the app fetches its key through its own secret poll.
Root: HKCU; Subkey: "Software\Classes\pairnets"; ValueType: string; ValueData: "URL:Pairnets"; Flags: uninsdeletekey
; The ticked boxes left for the app (written in [Code]); gone with Pairnets.
Root: HKCU; Subkey: "Software\Pairnets"; Flags: uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Pairnets\Setup"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\pairnets"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\pairnets\DefaultIcon"; ValueType: string; ValueData: "{app}\Pairnets.exe,0"
Root: HKCU; Subkey: "Software\Classes\pairnets\shell\open\command"; ValueType: string; ValueData: """{app}\Pairnets.exe"" ""%1"""

[Run]
; Windows 10/11 keep icons in a cache keyed by the exe path; refresh it so a replaced Pairnets.exe shows its new icon.
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Flags: runhidden nowait skipifdoesntexist
Filename: "{app}\Pairnets.exe"; Description: "Open Pairnets now"; Flags: nowait postinstall skipifsilent
; After an update from inside Pairnets (a silent install) start it again by itself.
Filename: "{app}\Pairnets.exe"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Stop Pairnets, then take it out of File Explorer (left side, right-click items, the folder's icon, the cloud folder);
; the files stay.
Filename: "{cmd}"; Parameters: "/C taskkill /IM Pairnets.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopPairnets"
Filename: "{app}\Pairnets.exe"; Parameters: "--unregister-explorer"; Flags: runhidden waituntilterminated; RunOnceId: "LeaveExplorer"

[Code]
const
  // The installer's id from when Pairnets was called Tether.
  TetherUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E7A3C4B-2D7F-4E5A-9B1C-7F3E2A9D4C10}_is1';
  // Pairnets' own entry (its AppId): there when Pairnets is already installed.
  PairnetsUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5DB04D62-50C8-4967-B220-449C44AC1BA3}_is1';

var
  // Pairnets was already installed when this Setup started (an upgrade): looked at before the install writes its entry.
  WasInstalled: Boolean;

function InitializeSetup(): Boolean;
begin
  WasInstalled := RegKeyExists(HKCU, PairnetsUninstallKey) or RegKeyExists(HKLM, PairnetsUninstallKey);
  Result := True;
end;

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

// With Files On-Demand some files may only be on the nest: Pairnets asks whether to go on (it explains what to do).
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (not UninstallSilent()) and FileExists(ExpandConstant('{app}\Pairnets.exe')) then
    if Exec(ExpandConstant('{app}\Pairnets.exe'), '--check-before-uninstall', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      Result := ResultCode <> 1;
end;

function OnOff(Task: String): String;
begin
  if WizardIsTaskSelected(Task) then
    Result := '1'
  else
    Result := '0';
end;

// The ticked boxes, for the app to take into its settings at its next start (once per install run, by the id).
procedure LeaveChoicesForTheApp();
var
  Where: String;
begin
  Where := 'here';
  if WizardIsTaskSelected('whereondemand') then
    Where := 'ondemand';
  if WizardIsTaskSelected('wherenest') then
    Where := 'nest';
  RegWriteStringValue(HKCU, 'Software\Pairnets\Setup', 'Choices',
    'explorerside=' + OnOff('explorerside') + ';explorermenu=' + OnOff('explorermenu') + ';syncticks=' + OnOff('syncticks') +
    ';where=' + Where + ';checkupdates=' + OnOff('checkupdates') + ';updatenest=' + OnOff('updatenest') + ';autostart=' + OnOff('autostart'));
  RegWriteStringValue(HKCU, 'Software\Pairnets\Setup', 'Id', GetDateTimeString('yyyymmddhhnnss', #0, #0));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemoveTether();
  // Only a first install: an upgrade (by hand or silent) never overwrites what was changed in Pairnets' Settings since.
  if (CurStep = ssPostInstall) and not WizardSilent() and not WasInstalled then
    LeaveChoicesForTheApp();
end;
