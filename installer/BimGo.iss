; BimGo installer (Inno Setup 6). Built by build\publish.ps1, which passes /DAppVersion and compiles from the
; artifacts it has just made (artifacts\app\, artifacts\bundle\BimGo.bundle\). Per user, no admin rights.
;
;   App:             %LocalAppData%\Programs\BimGo\ (fixed: every Revit year's add-in looks for BimGo.exe there)
;   BimGo for Revit: %AppData%\Autodesk\ApplicationPlugins\BimGo.bundle\ (Revit 2025 / 2026 / 2027 each load their own
;                    Contents\<year>\), offered when Revit 2025 or later is found
;   .bimgo + Start menu: owned by the app (BimGo.exe --register / --unregister), not by this script
;
; AppId, and the bundle's ProductCode / UpgradeCode (installer\PackageContents.xml), never change.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{A11D053D-9F32-4324-A8B8-C93ADD9E3AB1}
AppName=BimGo
AppVersion={#AppVersion}
AppVerName=BimGo {#AppVersion}
AppPublisher=Aussie BIM Guru
AppPublisherURL=https://github.com/aussieBIMguru/BimGo
AppSupportURL=https://github.com/aussieBIMguru/BimGo/tree/main/docs
AppUpdatesURL=https://github.com/aussieBIMguru/BimGo/releases
AppCopyright=Copyright 2026 Aussie BIM Guru (MIT-0)
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=BimGo
DefaultDirName={localappdata}\Programs\BimGo
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\BimGo.App\Resources\BimGo.ico
UninstallDisplayIcon={app}\BimGo.exe
UninstallDisplayName=BimGo {#AppVersion}
OutputBaseFilename=BimGo-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Revit or BimGo running is checked in [Code] (Revit holds the add-in dll; closing it for the user could lose work)
CloseApplications=no
RestartApplications=no

[Types]
Name: "full"; Description: "BimGo and BimGo for Revit"
Name: "app"; Description: "BimGo only (open .bimgo files without Revit)"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "app"; Description: "BimGo (the walkthrough app)"; Types: full app custom; Flags: fixed
Name: "revit"; Description: "BimGo for Revit 2025 / 2026 / 2027 (the add-in: Go, Export .bimgo, comment markers)"; Types: full custom; Check: RevitInstalled

[InstallDelete]
; The app folder holds nothing but the app: start clean (an older build may have left files a new one doesn't use)
Type: filesandordirs; Name: "{app}\*"
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\BimGo.bundle"
; Copies from developer builds before the bundle (Revit refuses an add-in id loaded twice)
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\BimGo.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\BimGo.addin"
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\BimGo.addin"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\BimGo"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\BimGo"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2027\BimGo"

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Components: app; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\bundle\BimGo.bundle\*"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BimGo.bundle"; Components: revit; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
; The .bimgo association and the Start-menu shortcut belong to the app
Filename: "{app}\BimGo.exe"; Parameters: "--register --quiet"; StatusMsg: "Registering .bimgo files..."; Flags: runhidden waituntilterminated
Filename: "{app}\BimGo.exe"; Description: "Start BimGo"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{app}\BimGo.exe"; Parameters: "--unregister --quiet"; RunOnceId: "UnregisterBimGo"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\BimGo.bundle"
Type: filesandordirs; Name: "{app}"

[Code]
const
  REVIT_YEARS = 3;

function RevitYear(Index: Integer): String;
begin
  Result := IntToStr(2025 + Index);
end;

{ Revit 2025 or later on this PC (the add-in is offered only then) }
function RevitInstalled: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to REVIT_YEARS - 1 do
  begin
    if FileExists(ExpandConstant('{commonpf64}\Autodesk\Revit ' + RevitYear(I) + '\Revit.exe')) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function IsProcessRunning(const FileName: String): Boolean;
var
  Locator, Service, Items: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Items := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = "' + FileName + '"');
    Result := Items.Count > 0;
  except
    Result := False; { can't tell: carry on (a locked file still makes setup ask) }
  end;
end;

{ BimGo.exe and Revit.exe hold files this setup replaces: ask the user to close them (never closed for them) }
function NothingRunning(const What: String): Boolean;
var
  Names: String;
begin
  Result := True;
  while True do
  begin
    Names := '';
    if IsProcessRunning('BimGo.exe') then Names := Names + #13#10 + '  - BimGo';
    if IsProcessRunning('Revit.exe') then Names := Names + #13#10 + '  - Revit';
    if Names = '' then Exit;
    if MsgBox('Please close these before ' + What + ' (save your work first):' + Names + #13#10#13#10 +
              'Then choose Retry.', mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := NothingRunning('installing BimGo');
end;

function InitializeUninstall: Boolean;
begin
  Result := NothingRunning('removing BimGo');
end;

{ After uninstalling: optionally remove settings, logs and caches. Per-model data (comments, bookmarks, measurements,
  clash results...) in %LocalAppData%\BimGo\Models is always kept, as are files beside Revit models. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Local: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if UninstallSilent then Exit;
  if MsgBox('Also remove your BimGo settings and logs?' + #13#10#13#10 +
            'This removes your options, key bindings and logs. Your comments, bookmarks and other per-model data ' +
            '(in %LocalAppData%\BimGo\Models) are kept either way.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then Exit;

  DelTree(ExpandConstant('{userappdata}\BimGo'), True, True, True);
  Local := ExpandConstant('{localappdata}\BimGo');
  DelTree(Local + '\Logs', True, True, True);
  DelTree(Local + '\App', True, True, True);
  DelTree(Local + '\Sessions', True, True, True);
  DelTree(Local + '\Families', True, True, True);
  DeleteFile(Local + '\LastLive.json');
end;
