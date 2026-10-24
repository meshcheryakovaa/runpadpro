#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
[Setup]
AppId={{453BEF71-5CFA-43CE-A117-DF036F7B9F42}
AppName=Runpad Pro Modern
AppVersion={#AppVersion}
DefaultDirName={autopf}\RunpadPro
DefaultGroupName=Runpad Pro
OutputDir={#OutputDir}
OutputBaseFilename=RunpadPro-Setup-{#AppVersion}-win-x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\app\shell\Runpad.Shell.exe
SetupLogging=yes
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked
Name: "autorun"; Description: "Запускать агент при входе пользователей Windows"; Flags: unchecked
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Runpad Pro"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\RunpadPro.vbs"""; WorkingDir: "{app}"; IconFilename: "{app}\app\shell\Runpad.Shell.exe"
Name: "{group}\Runpad CLI"; Filename: "{cmd}"; Parameters: "/K """"{app}\runpad.cmd"" help"""; WorkingDir: "{app}"
Name: "{commondesktop}\Runpad Pro"; Filename: "{sys}\wscript.exe"; Parameters: """{app}\RunpadPro.vbs"""; Tasks: desktopicon
[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RunpadProAgent"; ValueData: """{app}\app\agent\Runpad.Agent.exe"""; Tasks: autorun; Flags: uninsdeletevalue
[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop RunpadPro"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete RunpadPro"; Flags: runhidden waituntilterminated
[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
