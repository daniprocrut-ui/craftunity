; Craftunity Launcher — установщик Inno Setup 6
; Собирает папку лаунчера в %LOCALAPPDATA%\Craftunity Launcher, делает ярлыки и деинсталлятор.
; Сборка папки: pyinstaller Craftunity_launcher_onedir.spec (dist\Craftunity_launcher)

#define AppName "Craftunity Launcher"
#define AppShortName "Craftunity"
#define SourceDir "..\dist\Craftunity_launcher"
#define SourceExe "..\dist\Craftunity_launcher\Craftunity_launcher.exe"

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
UninstallDisplayIcon={app}\Craftunity_launcher.exe
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
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Craftunity_launcher.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Craftunity_launcher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Craftunity_launcher.exe"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Craftunity_launcher.exe"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{app}\_internal"
Type: filesandordirs; Name: "{app}\ui"
Type: filesandordirs; Name: "{app}\keys"

[Code]
{ Перед обновлением закрываем запущенный лаунчер, чтобы файлы не были заняты. }
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM Craftunity_launcher.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(600);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { Ярлык в меню «Пуск» с тем же именем процесса для поиска Windows }
    ;
  end;
end;
