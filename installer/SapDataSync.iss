#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif

#define AppName "SAP DataSync"
#define AppPublisher "SAP DataSync Team"
#define AppExeName "SapDataSync.Launcher.exe"
#define SourceRoot ".."

[Setup]
AppId={{1D429993-E942-4DA9-986E-4A3206041B69}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\SapDataSync
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=SapDataSync-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài màn hình"; GroupDescription: "Biểu tượng bổ sung:"; Flags: unchecked

[Files]
Source: "{#SourceRoot}\artifacts\launcher\win-x64\SapDataSync.Launcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\compose.yaml"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\.env.example"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\global.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\NuGet.Config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\tools\backup-database.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#SourceRoot}\tools\test-external-sqlserver.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#SourceRoot}\docker\*"; DestDir: "{app}\docker"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\database\scripts\*"; DestDir: "{app}\database\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\src\WebApi\*"; DestDir: "{app}\src\WebApi"; Excludes: "bin\*,obj\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\src\EtlWorker\*"; DestDir: "{app}\src\EtlWorker"; Excludes: "__pycache__\*,.pytest_cache\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{app}\data\source"
Name: "{app}\data\uploads"
Name: "{app}\data\archive"

[Icons]
Name: "{group}\SAP DataSync"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\Hướng dẫn vận hành"; Filename: "{app}\docs\HUONG_DAN_VAN_HANH.txt"
Name: "{autodesktop}\SAP DataSync"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Mở SAP DataSync Launcher"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    MsgBox(
      'Trình gỡ cài đặt chỉ xóa chương trình và shortcut.' + #13#10 + #13#10 +
      'Docker volume, SQL Server, database, file .env và dữ liệu Excel không bị xóa tự động. ' +
      'Hãy dừng hệ thống bằng Launcher trước khi gỡ cài đặt.',
      mbInformation,
      MB_OK);
  end;
end;
