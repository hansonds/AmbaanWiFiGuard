; Inno Setup script - built by GitHub Actions. Values are passed in with /D from the workflow.
#ifndef AppName
  #define AppName "Ambaan WiFi Guard"
#endif
#ifndef AppId
  #define AppId "AmbaanWiFiGuard"
#endif
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef AppPublisher
  #define AppPublisher "Phoenicx"
#endif
#ifndef AppUrl
  #define AppUrl "https://phoenicx.org"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename={#AppId}-Setup-{#AppVersion}
SetupIconFile=..\app\Assets\ambaan.ico
UninstallDisplayIcon={app}\{#AppId}.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[InstallDelete]
; version 1.x was PowerShell scripts in an "app" folder
Type: filesandordirs; Name: "{app}\app"

[Files]
Source: "..\build\{#AppId}.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\installed.flag"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppId}.exe"
Name: "{group}\Full Wi-Fi test"; Filename: "{app}\{#AppId}.exe"; Parameters: "--analyzer"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppId}.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppId}"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppId}.exe"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userstartup}\Ambaan WiFi Guard.lnk"

[Code]
// Close a running Ambaan first (new EXE, or the old PowerShell widget), so its files can be replaced / removed.
procedure CloseRunningApp();
var R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppId}.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process -Filter ''Name=''''powershell.exe'''''' | Where-Object { $_.CommandLine -like ''*WiFi-Floating-Widget.ps1*'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"',
    '', SW_HIDE, ewWaitUntilTerminated, R);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp();
  Result := True;
end;
