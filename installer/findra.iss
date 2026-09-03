; Findra's installer.
;
; Three rules here are load-bearing and each has a test in
; tests/Findra.Tests/Build/InstallerScriptTests.cs:
;
;   1. The install directory carries no version. The scheduled task stores an absolute path to
;      findra.exe, so a versioned directory breaks name search on every upgrade.
;   2. The processes are stopped BEFORE files are replaced. Inno's CloseApplications only closes
;      windowed applications and two of Findra's three processes have no window.
;   3. Uninstalling runs findra --uninstall, which is what removes the HighestAvailable scheduled
;      task. An uninstaller that only deletes files leaves an elevated logon task pointing at a
;      binary that is gone, which the specification calls a defect.

#ifndef AppVersion
  #error AppVersion must be passed in: iscc /DAppVersion=<major.minor.patch> /DPublishDir=..\publish\win-x64 findra.iss
#endif
#ifndef PublishDir
  #error PublishDir must be passed in
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{7D2E4C6A-3B51-4F0E-9A77-1C8E5B6D40A2}
AppName=Findra
AppVersion={#AppVersion}
AppPublisher=blakazulu
AppPublisherURL=https://github.com/blakazulu/findra
AppSupportURL=https://github.com/blakazulu/findra/issues
; No version in the path. See rule 1 above.
DefaultDirName={autopf}\Findra
DefaultGroupName=Findra
DisableProgramGroupPage=yes
; Administrator rights, because the product needs them once and the uninstaller needs them to
; remove the scheduled task.
PrivilegesRequired=admin
; "x64compatible" and "arm64" in this form are Inno Setup 6.3 syntax. The release workflow pins
; the chocolatey package to 6.3 or newer for exactly this reason; on 6.2 the line is a compile
; error rather than a silent misbuild, which is the failure mode to prefer.
MinVersion=10.0
ArchitecturesAllowed={#Arch}compatible
ArchitecturesInstallIn64BitMode={#Arch}compatible
OutputDir=Output
OutputBaseFilename=findra-{#AppVersion}-{#Arch}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\findra.exe
UninstallDisplayName=Findra

[Files]
; The whole self-contained publish. No models: they are downloaded on first run into
; %LOCALAPPDATA%\Findra\models, and an installer carrying them would be 3 GB and would put them
; where the uninstaller's keep-by-default rule does not reach.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Findra"; Filename: "{app}\findra.exe"

[Run]
Filename: "{app}\findra.exe"; Description: "Start Findra"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Runs while findra.exe still exists, before the uninstaller removes any file. This is what
; removes the scheduled task, the autostart entry and the running processes. Exactly one of the
; two runs, decided in InitializeUninstall below.
Filename: "{app}\findra.exe"; Parameters: "--uninstall --quiet"; Flags: runhidden; RunOnceId: "findra-uninstall"; Check: KeepWanted
Filename: "{app}\findra.exe"; Parameters: "--uninstall --purge --quiet"; Flags: runhidden; RunOnceId: "findra-purge"; Check: PurgeWanted

[Code]
var
  Purge: Boolean;

function StopFindra(): Boolean;
var
  code: Integer;
begin
  // --stop, not CloseApplications: the name helper is headless and elevated and the indexer is a
  // hidden child, and neither has a window for Inno to close.
  Result := True;
  if FileExists(ExpandConstant('{app}\findra.exe')) then
    Exec(ExpandConstant('{app}\findra.exe'), '--stop', '', SW_HIDE, ewWaitUntilTerminated, code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Before any file is copied. See rule 2.
  StopFindra();
  Result := '';
end;

procedure RecordInstallSource();
var
  source: String;
begin
  // winget passes /INSTALLSOURCE=winget through the manifest's InstallerSwitches. Anything else
  // is somebody running the installer by hand. The app reads this once, at first run, and never
  // guesses again (spec 9b).
  source := 'installer';
  if CompareText(ExpandConstant('{param:INSTALLSOURCE|}'), 'winget') = 0 then
    source := 'winget';
  SaveStringToFile(ExpandConstant('{app}\installed-by.txt'), source, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RecordInstallSource();
end;

function InitializeUninstall(): Boolean;
var
  app, reportPath, report: String;
  code: Integer;
  Form: TSetupForm;
  Body: TNewStaticText;
  Box: TNewCheckBox;
  OkButton, CancelButton: TNewButton;
begin
  // The first thing the uninstaller does, before [UninstallRun] evaluates its Check routines.
  Result := True;
  Purge := False;
  if UninstallSilent() then Exit;

  app := ExpandConstant('{app}\findra.exe');
  if not FileExists(app) then Exit;

  // Spec 2a: "The prompt states the MEASURED size it would free ... not a vague warning."
  // --dry-run --quiet writes that report to %TEMP%\findra-uninstall.txt precisely because an Inno
  // script cannot capture a child process's standard output, and the alternative - the installer
  // estimating the size itself - is the vague warning the specification rejects.
  report := '';
  reportPath := ExpandConstant('{%TEMP}') + '\findra-uninstall.txt';
  DeleteFile(reportPath);
  Exec(app, '--uninstall --dry-run --quiet', '', SW_HIDE, ewWaitUntilTerminated, code);
  LoadStringFromFile(reportPath, report);

  // A CHECKBOX, not a message box. Spec 2a and PRIVACY.md both promise a checkbox in the
  // uninstaller, and an Inno uninstaller has no wizard pages - so it is a custom form.
  Form := CreateCustomForm();
  Form.Caption := 'Remove Findra';
  Form.ClientWidth := ScaleX(520);
  Form.ClientHeight := ScaleY(300);
  Form.Position := poScreenCenter;

  Body := TNewStaticText.Create(Form);
  Body.Parent := Form;
  Body.Left := ScaleX(16);
  Body.Top := ScaleY(16);
  Body.Width := Form.ClientWidth - ScaleX(32);
  Body.Height := ScaleY(200);
  Body.WordWrap := True;
  Body.Caption := report;

  Box := TNewCheckBox.Create(Form);
  Box.Parent := Form;
  Box.Left := ScaleX(16);
  Box.Top := ScaleY(224);
  Box.Width := Form.ClientWidth - ScaleX(32);
  Box.Caption := 'Also delete the downloaded models, the index and my settings';
  // Unticked. Keeping is the default because reinstalling is common and re-downloading gigabytes
  // is expensive; a box that starts ticked is the same as no box for anyone who clicks through.
  Box.Checked := False;

  OkButton := TNewButton.Create(Form);
  OkButton.Parent := Form;
  OkButton.Width := ScaleX(90);
  OkButton.Height := ScaleY(26);
  OkButton.Left := Form.ClientWidth - ScaleX(196);
  OkButton.Top := Form.ClientHeight - ScaleY(42);
  OkButton.Caption := 'Remove';
  OkButton.ModalResult := mrOk;
  OkButton.Default := True;

  CancelButton := TNewButton.Create(Form);
  CancelButton.Parent := Form;
  CancelButton.Width := ScaleX(90);
  CancelButton.Height := ScaleY(26);
  CancelButton.Left := Form.ClientWidth - ScaleX(100);
  CancelButton.Top := OkButton.Top;
  CancelButton.Caption := 'Cancel';
  CancelButton.ModalResult := mrCancel;
  CancelButton.Cancel := True;

  Form.ActiveControl := OkButton;
  if Form.ShowModal() = mrOk then Purge := Box.Checked else Result := False;
  Form.Free();
end;

function PurgeWanted(): Boolean;
begin
  Result := Purge;
end;

function KeepWanted(): Boolean;
begin
  // Keeping is the default and the silent answer. Spec 2a: reinstalling is common and
  // re-downloading gigabytes is expensive, so deleting is the thing somebody has to ask for.
  Result := not Purge;
end;
