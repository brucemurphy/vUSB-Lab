#ifndef AppVersion
  #error AppVersion must be supplied by Publish-Release.ps1
#endif

[Setup]
AppId={{C5CD1C97-0C70-491D-93BC-54C3B2C54E67}
AppName=vUSB Lab
AppVersion={#AppVersion}
AppPublisher=Bruce Murphy
AppPublisherURL=https://github.com/brucemurphy/vUSB-Lab
AppUpdatesURL=https://github.com/brucemurphy/vUSB-Lab/releases
DefaultDirName={autopf}\vUSB Lab
DefaultGroupName=vUSB Lab
DisableProgramGroupPage=yes
PrivilegesRequired=admin
#if Architecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputBaseFilename=vUSB-Lab-v{#AppVersion}-win-{#Architecture}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\vUSB LAB.exe
SetupLogging=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\vUSB Lab"; Filename: "{app}\vUSB LAB.exe"

[Run]
Filename: "{app}\vUSB LAB.exe"; Description: "Launch vUSB Lab"; Flags: nowait postinstall skipifsilent runascurrentuser; Check: not IsUpdate
Filename: "{app}\vUSB LAB.exe"; Flags: nowait runascurrentuser; Check: IsUpdate

[Code]
function OpenProcess(Access: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ProcessId: Integer;
  Handle: THandle;
  WaitResult: LongWord;
begin
  Result := '';
  ProcessId := StrToIntDef(ExpandConstant('{param:WAITPID|0}'), 0);
  if ProcessId > 0 then
  begin
    Handle := OpenProcess($00100000, False, ProcessId);
    if Handle <> 0 then
    begin
      WaitResult := WaitForSingleObject(Handle, 60000);
      CloseHandle(Handle);
      if WaitResult <> 0 then
      begin
        Result := 'vUSB Lab did not exit. Close it from the tray and retry the update.';
        Exit;
      end;
    end;
  end;
  if CheckForMutexes('Local\vUSB-LAB') then
    Result := 'Please exit vUSB Lab from its tray menu before installing. Your settings will be preserved.';
end;
