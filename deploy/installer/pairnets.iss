; Optional per-user installer for the Pairnets client (built by release.yml with Inno Setup 6).
; No admin rights needed; autostart stays a user setting inside Pairnets.
; It is only seen on the first install: Pairnets updates itself by running it hidden (/VERYSILENT), and started again by
; hand over an installed Pairnets it skips its pages. It never deletes or rewrites the app's data
; (%AppData%\Pairnets, %LocalAppData%\Pairnets): no [InstallDelete] or [UninstallDelete] for them, ever.
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
; After an update from inside Pairnets (a hidden install) start it again by itself; --after-update makes it show a
; small "updated" note instead of opening its window.
Filename: "{app}\Pairnets.exe"; Parameters: "--after-update"; Flags: nowait; Check: WizardSilent

[UninstallRun]
; Stop Pairnets, then take it out of File Explorer (left side, right-click items, the folder's icon, the cloud folder);
; the files stay.
Filename: "{cmd}"; Parameters: "/C taskkill /IM Pairnets.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopPairnets"
Filename: "{app}\Pairnets.exe"; Parameters: "--unregister-explorer"; Flags: runhidden waituntilterminated; RunOnceId: "LeaveExplorer"

[Code]
const
  // The installer's id from when Pairnets was called Tether.
  TetherUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E7A3C4B-2D7F-4E5A-9B1C-7F3E2A9D4C10}_is1';
  // This installer's own entry (the AppId above): there when Pairnets is installed already.
  PairnetsUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5DB04D62-50C8-4967-B220-449C44AC1BA3}_is1';

var
  // Pairnets is installed already: this run is an update, not the first install.
  WasInstalled: Boolean;

// The installer is for the first install. Started again over an installed Pairnets (by hand), it is a one-click
// update: no pages to go through, the same folder and the earlier choices, and the app's settings are left alone.
function InitializeSetup(): Boolean;
begin
  WasInstalled := RegKeyExists(HKCU, PairnetsUninstallKey) or RegKeyExists(HKLM, PairnetsUninstallKey);
  Result := True;
end;

// True only for the first install on this PC. Anything that writes choices into the app's settings must check it,
// so an update never resets them.
function IsFirstInstall(): Boolean;
begin
  Result := not WasInstalled;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := WasInstalled and ((PageID = wpWelcome) or (PageID = wpSelectDir) or (PageID = wpSelectProgramGroup)
    or (PageID = wpSelectTasks) or (PageID = wpReady));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if WasInstalled and (CurPageID = wpFinished) then
    WizardForm.FinishedLabel.Caption := 'Pairnets is updated to {#AppVersion}. You are still signed in and your settings are kept.';
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    RemoveTether();
end;
