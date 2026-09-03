; Findra's installer.
;
; Five rules here are load-bearing and each has a test in
; tests/Findra.Tests/Build/InstallerScriptTests.cs:
;
;   1. The install directory carries no version. The scheduled task stores an absolute path to
;      findra.exe, so a versioned directory breaks name search on every upgrade.
;   2. The processes are stopped BEFORE files are replaced. Inno's CloseApplications only closes
;      windowed applications and two of Findra's three processes have no window.
;   3. Uninstalling runs findra --uninstall, which is what removes the HighestAvailable scheduled
;      task. An uninstaller that only deletes files leaves an elevated logon task pointing at a
;      binary that is gone, which the specification calls a defect.
;   4. QuietUninstallString is deleted after the install. Inno registers it automatically and
;      Windows 11's Settings prefers it, which runs the uninstaller silently and skips the
;      checkbox that asks about gigabytes of somebody's data.
;   5. That uninstall is run from CurUninstallStepChanged and NOT from [UninstallRun]. The
;      checkbox is answered during the uninstall; an [UninstallRun] entry is decided during the
;      INSTALL. See the comment on CurUninstallStepChanged for what that cost.

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
; Written out per architecture rather than built by pasting "compatible" onto {#Arch}. Inno's
; identifiers are not a regular family: x64 has "x64compatible", arm64 does NOT have an
; "arm64compatible" - the identifier is just "arm64". Appending the suffix to both gave the x64
; leg a valid word and the arm64 leg one that does not exist, which is an ISCC error, which fails
; the build job, which means a tag produces no release for EITHER architecture. The comment above
; named the correct pair while the code below contradicted it.
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir=Output
OutputBaseFilename=findra-{#AppVersion}-{#Arch}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
; The mark, on setup.exe itself and in the wizard's title bar. Same file the application icon is
; compiled from, so the installer a stranger downloads carries the icon they are about to get.
SetupIconFile=..\assets\icon\findra.ico
; And in the corner of every wizard page, so the thing being installed is on screen throughout
; rather than only on the file somebody downloaded. Inno scales one bitmap to whatever the
; display is set to; 128 px is what it reads at 250%.
WizardSmallImageFile=..\assets\icon\findra-wizard.png
; Add/Remove Programs reads its icon out of this binary, which carries the mark as a PE resource
; (see ApplicationIcon in src/Findra/Findra.csproj). Nothing here needs the .ico a second time.
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

[UninstallDelete]
; installed-by.txt is written by RecordInstallSource below, from [Code], with SaveStringToFile.
; Inno only removes what it recorded in its own uninstall log, and a file written by a script is
; not in it - so without this line the file survives every uninstall, the {app} directory is not
; empty, and Inno leaves the directory behind too. A real uninstall on a real machine left
; "C:\Program Files\Findra" holding one nine-byte file.
Type: files; Name: "{app}\installed-by.txt"

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

/// The Add/Remove Programs key Inno registers for this application. The GUID is read from the
/// AppId directive above at compile time rather than typed a second time - two copies of a GUID in
/// one file is how they come to differ, and a key built from the wrong one deletes nothing, says
/// nothing, and is only ever noticed on somebody else's machine. ExpandConstant turns Inno's
/// doubled leading brace back into the single one the key name carries.
function UninstallKey(): String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' +
            ExpandConstant('{#SetupSetting("AppId")}') + '_is1';
end;

/// Inno registers QuietUninstallString beside UninstallString on its own, and Windows 11's
/// Settings > Apps prefers it. The uninstaller then starts with /SILENT, UninstallSilent() is
/// true, and InitializeUninstall returns before it has built anything - so the checkbox is never
/// shown, Purge stays False, and the models, the index and the settings are all kept from
/// somebody who believed they had asked for Findra to be gone. That is the route nearly everybody
/// takes, and it is what made "a checkbox in the uninstaller" untrue in practice.
///
/// Removing the value costs nothing that was worth having: a scripted removal that really wants
/// no questions still has findra.exe --uninstall --purge --quiet, which says what it will do on
/// the command line instead of guessing at it.
procedure ForgetTheQuietUninstall();
var
  key: String;
  gone: Boolean;
begin
  key := UninstallKey();
  // Named views rather than the unsuffixed constant. Inno writes the key in the 64-bit view
  // because this install runs in 64-bit mode, and a value deleted from the other hive is a silent
  // no-op with no symptom until an uninstall months later asks nothing and keeps everything.
  if Is64BitInstallMode() then
    gone := RegDeleteValue(HKLM64, key, 'QuietUninstallString')
  else
    gone := RegDeleteValue(HKLM32, key, 'QuietUninstallString');
  // Not finding it is not a failure - a repair install runs this again - but which of the two
  // happened is the answer to "why did Settings not ask", and that answer took ten minutes to
  // work out the first time because nothing anywhere had written it down.
  if gone then
    Log('QuietUninstallString removed from ' + key)
  else
    Log('no QuietUninstallString under ' + key + ', nothing to remove');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RecordInstallSource();
    // After the install rather than during it: Inno writes the uninstall key as part of the
    // install, so a deletion from ssInstall removes a value that is then written again.
    ForgetTheQuietUninstall();
  end;
end;

/// Everything between "It will keep:" and the blank line that ends it, with its column
/// alignment intact. The report goes on to say "Run findra --uninstall --purge to delete those
/// too", which is right in a terminal and wrong here, where a checkbox does that job and would be
/// contradicted by the sentence above it.
function KeepBlock(const report: String): String;
var
  head, tail: Integer;
  rest: String;
begin
  Result := report;
  head := Pos('It will keep:', report);
  if head = 0 then Exit;
  rest := Copy(report, head + Length('It will keep:'), Length(report));
  // The block ends at the first blank line after it. Two line endings in a row, so this survives
  // a report written with either CRLF or LF.
  tail := Pos(#10#10, rest);
  if tail = 0 then tail := Pos(#13#10#13#10, rest);
  if tail > 0 then rest := Copy(rest, 1, tail - 1);
  Result := Trim(rest);
end;

/// " and free 908 MB", or an empty string when the report does not say. Never computed here: the
/// measured size is the product's own and there is exactly one measurement behind every surface
/// that quotes it, which is the whole reason the installer runs --dry-run instead of adding up
/// folders itself.
function Freed(const report: String): String;
var
  at, stop: Integer;
  size: String;
begin
  Result := '';
  at := Pos('freeing ', report);
  if at = 0 then Exit;
  size := Copy(report, at + Length('freeing '), 24);
  // Cut at the LINE END and then drop a trailing full stop, never at the first '.' - the figure
  // is "1.42 GB." as often as "908 MB.", and cutting at the first dot turns the first of those
  // into "1". Checked against the file the product actually writes, not against the format it
  // was assumed to have.
  stop := Pos(#13, size);
  if stop = 0 then stop := Pos(#10, size);
  if stop > 0 then size := Copy(size, 1, stop - 1);
  size := Trim(size);
  while (size <> '') and (size[Length(size)] = '.') do size := Copy(size, 1, Length(size) - 1);
  if size <> '' then Result := ' and free ' + size;
end;

function InitializeUninstall(): Boolean;
var
  app, reportPath: String;
  // AnsiString, and not String, because that is the exact type LoadStringFromFile's var parameter
  // is declared with. Inno 6 is Unicode-only, so String is UnicodeString, and PascalScript demands
  // exact type identity for a var parameter - passing a String here is an ISCC compile error, not
  // a conversion. SaveStringToFile above takes its text as a const parameter, which does convert,
  // which is why only this one had to change.
  report: AnsiString;
  code: Integer;
  Form: TSetupForm;
  Head, Intro, Kept, Body, Warn: TNewStaticText;
  Box: TNewCheckBox;
  OkButton, CancelButton: TNewButton;
begin
  // The first thing the uninstaller does, before CurUninstallStepChanged runs the uninstall.
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
  // {%TMP|...} first, because the two sides disagree about which variable wins: .NET's
  // Path.GetTempPath - which wrote this file - reads TMP before TEMP, while Inno's {%TEMP}
  // reads only TEMP. Where a machine sets them differently, reading TEMP alone finds nothing
  // and the dialog silently loses the measured size it exists to show.
  reportPath := ExpandConstant('{%TMP|' + ExpandConstant('{%TEMP}') + '}') + '\findra-uninstall.txt';
  DeleteFile(reportPath);
  Exec(app, '--uninstall --dry-run --quiet', '', SW_HIDE, ewWaitUntilTerminated, code);
  LoadStringFromFile(reportPath, report);

  // A CHECKBOX, not a message box. Spec 2a and PRIVACY.md both promise a checkbox in the
  // uninstaller, and an Inno uninstaller has no wizard pages - so it is a custom form.
  //
  // The report is written for a TERMINAL. Wrapping it into a label destroyed the column the sizes
  // are aligned in, and it ends with "Run findra --uninstall --purge to delete those too", which
  // is a command-line instruction sitting directly above a checkbox that does that very thing.
  // KeepBlock takes the part a person needs here and Freed takes the total for the checkbox. The
  // numbers are still the product's own measurement, read out of the file it wrote, and are never
  // estimated here.
  //
  // CreateCustomForm takes the client size and two sizing flags; it is not a zero-argument call,
  // and the size cannot be set afterwards through ClientWidth/ClientHeight. Both sizing flags are
  // False because every control below sits at a fixed offset. Inno's own Examples\CodeClasses.iss
  // is the reference for this signature.
  Form := CreateCustomForm(ScaleX(560), ScaleY(374), False, False);
  Form.Caption := 'Remove Findra';
  Form.Position := poScreenCenter;
  Form.Font.Name := 'Segoe UI';
  Form.Font.Size := 9;

  Head := TNewStaticText.Create(Form);
  Head.Parent := Form;
  Head.Left := ScaleX(20);
  Head.Top := ScaleY(18);
  Head.Font.Name := 'Segoe UI';
  Head.Font.Size := 14;
  Head.Caption := 'Remove Findra';

  Intro := TNewStaticText.Create(Form);
  Intro.Parent := Form;
  Intro.Left := ScaleX(20);
  Intro.Top := ScaleY(54);
  Intro.Width := Form.ClientWidth - ScaleX(40);
  Intro.Height := ScaleY(36);
  Intro.WordWrap := True;
  Intro.Caption := 'Findra will stop, remove the scheduled task that starts it at sign-in, and ' +
                   'delete its program files.';

  Kept := TNewStaticText.Create(Form);
  Kept.Parent := Form;
  Kept.Left := ScaleX(20);
  Kept.Top := ScaleY(100);
  Kept.Caption := 'It keeps these, unless you say otherwise:';

  Body := TNewStaticText.Create(Form);
  Body.Parent := Form;
  Body.Left := ScaleX(20);
  Body.Top := ScaleY(124);
  Body.Width := Form.ClientWidth - ScaleX(40);
  Body.Height := ScaleY(92);
  // Fixed width, and NOT wrapped. The sizes were aligned into columns by the process that
  // measured them, and a proportional wrapped label throws that alignment away - which is most of
  // why this dialog read as an afterthought.
  Body.Font.Name := 'Consolas';
  Body.Font.Size := 9;
  Body.WordWrap := False;
  Body.Caption := KeepBlock(String(report));

  Box := TNewCheckBox.Create(Form);
  Box.Parent := Form;
  Box.Left := ScaleX(20);
  Box.Top := ScaleY(240);
  Box.Width := Form.ClientWidth - ScaleX(40);
  Box.Height := ScaleY(22);
  // One line: TNewCheckBox has no WordWrap, so a caption longer than the box is simply clipped.
  // The consequence goes in the note under it, where there is room to say it properly.
  Box.Caption := 'Also delete my index, my settings and the models' + Freed(String(report));
  // Unticked. Keeping is the default because reinstalling is common and re-downloading gigabytes
  // is expensive; a box that starts ticked is the same as no box for anyone who clicks through.
  Box.Checked := False;

  Warn := TNewStaticText.Create(Form);
  Warn.Parent := Form;
  Warn.Left := ScaleX(40);
  Warn.Top := ScaleY(266);
  Warn.Width := Form.ClientWidth - ScaleX(60);
  Warn.Height := ScaleY(34);
  Warn.WordWrap := True;
  Warn.Caption := 'Leave this unticked if you may reinstall. Downloading the models again can ' +
                  'take a long time, and a finished index does not have to be built twice.';

  OkButton := TNewButton.Create(Form);
  OkButton.Parent := Form;
  OkButton.Width := ScaleX(104);
  OkButton.Height := ScaleY(30);
  OkButton.Left := Form.ClientWidth - ScaleX(236);
  OkButton.Top := Form.ClientHeight - ScaleY(52);
  OkButton.Caption := 'Remove';
  OkButton.ModalResult := mrOk;
  OkButton.Default := True;

  CancelButton := TNewButton.Create(Form);
  CancelButton.Parent := Form;
  CancelButton.Width := ScaleX(104);
  CancelButton.Height := ScaleY(30);
  CancelButton.Left := Form.ClientWidth - ScaleX(124);
  CancelButton.Top := OkButton.Top;
  CancelButton.Caption := 'Cancel';
  CancelButton.ModalResult := mrCancel;
  CancelButton.Cancel := True;

  Form.ActiveControl := OkButton;
  if Form.ShowModal() = mrOk then Purge := Box.Checked else Result := False;
  Form.Free;
end;

/// The uninstall itself: stopping the three processes, removing the HighestAvailable scheduled
/// task and the autostart entry, and - only if the checkbox above was ticked - deleting the
/// models, the index and the settings. usUninstall is before any file is removed, so findra.exe
/// is still there to be run.
///
/// This is [Code] and not [UninstallRun] because of one sentence in Inno's own install order:
/// "The entries in [UninstallRun] are stored in the uninstall log." They are recorded during the
/// INSTALL, which is when their Check parameters are evaluated - and Purge is False then, because
/// the person has not been asked yet and will not be for weeks. So the keep entry was written
/// into unins000.dat and the purge entry never was, and no answer given during the uninstall
/// could reach either of them. The checkbox was drawn, was ticked, was read into Purge, and
/// decided nothing at all: a real uninstall that asked for the models to go left all 2.9 GB of
/// them on the disk and reported success.
///
/// Verified on the shipped artefact rather than reasoned about. A byte search of a real
/// unins000.dat finds "--uninstall --quiet" among the recorded entries at the end of the file and
/// does not contain "--uninstall --purge --quiet" anywhere in it.
///
/// The general rule is worth more than the fix: a decision taken during the uninstall cannot be
/// carried by anything the installer wrote down.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  app: String;
  code: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;

  app := ExpandConstant('{app}\findra.exe');
  // A repair, a half-finished install, or somebody who deleted the folder by hand. There is no
  // uninstall to run and nothing here can put the scheduled task right; Inno carries on and
  // removes what it recorded.
  if not FileExists(app) then Exit;

  // Keeping is the default and the silent answer. Spec 2a: reinstalling is common and
  // re-downloading gigabytes is expensive, so deleting is the thing somebody has to ask for.
  if Purge then
    Exec(app, '--uninstall --purge --quiet', '', SW_HIDE, ewWaitUntilTerminated, code)
  else
    Exec(app, '--uninstall --quiet', '', SW_HIDE, ewWaitUntilTerminated, code);
end;
