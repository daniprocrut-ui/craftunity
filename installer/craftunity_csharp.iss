; Craftunity Launcher — установщик Inno Setup 6
; Ставит C# окно лаунчера вместе с урезанным Python-окружением.
; Сборка дистрибутива: dist\CraftunityLauncher_app

#define AppName "Craftunity Launcher"
#define SourceDir "..\dist\CraftunityLauncher_app"

[Setup]
AppId={{B8F1C0D2-6A4E-4B7C-9E31-7C5A2D9F8E10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Craftunity
DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Craftunity_Setup_{#AppVersion}
SetupIconFile=..\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\CraftunityLauncher.exe
CloseApplications=yes
RestartApplications=no
DisableWelcomePage=no
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} установщик
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\CraftunityLauncher.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\CraftunityLauncher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CraftunityLauncher.exe"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\CraftunityLauncher.exe"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "cleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\python"
Type: filesandordirs; Name: "{app}\craftunity"
Type: filesandordirs; Name: "{app}\ui"
Type: filesandordirs; Name: "{app}\keys"
Type: files; Name: "{app}\run.py"

[Code]
{ Закрываем запущенные процессы лаунчера и Python уже на этапе установки файлов.
  В InitializeSetup этого делать нельзя: окно ещё не появилось, а taskkill висит. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM CraftunityLauncher.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM python.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);
  Result := '';
end;

{ Убираем мёртвый lock-файл, чтобы новый лаунчер сразу запустился. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  LockPath: String;
begin
  if CurStep = ssPostInstall then
  begin
    LockPath := ExpandConstant('{userappdata}\Craftunity\launcher.lock');
    DeleteFile(LockPath);
  end;
end;
