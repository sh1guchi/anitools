; Установщик anitools (docs/PLAN.md, этап 9). Без прав администратора: в %LOCALAPPDATA%\Programs\anitools,
; ярлык в «Пуске», удаление — через «Параметры → Приложения». Галочка «Команда ani»: папка программы в PATH
; пользователя и ani.exe — жёсткая ссылка на Anitools.exe (та же программа, места не занимает).
; Настройки, логи и поставленные из программы ffmpeg/MKVToolNix (%APPDATA%\anitools, %LOCALAPPDATA%\anitools)
; при удалении не трогаются.
;
; Сборка (Inno Setup 6.3+), из корня репозитория, после dotnet publish в artifacts\publish:
;   ISCC.exe /DAppVersion=1.0.0 installer\anitools.iss   → artifacts\installer\anitools-setup.exe
; Тихая установка: anitools-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/TASKS="anicommand"] [/nolaunch=1]

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6E0C3D52-8B7A-4F1D-9A24-3C5B1E7F0A96}
AppName=anitools
AppVersion={#AppVersion}
AppVerName=anitools {#AppVersion}
AppPublisher=shiguchi
AppPublisherURL=https://github.com/sh1guchi/anitools
AppSupportURL=https://github.com/sh1guchi/anitools/issues
AppUpdatesURL=https://github.com/sh1guchi/anitools/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\anitools
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=anitools-setup
SetupIconFile=..\src\Anitools.App\Assets\anitools.ico
UninstallDisplayIcon={app}\Anitools.exe
UninstallDisplayName=anitools
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; PATH меняется — Windows сообщается, новые консоли видят команду ani сразу
ChangesEnvironment=yes
; Открытый anitools закрывается перед заменой файлов (и при тихом обновлении из самой программы)
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "anicommand"; Description: "Команда ani — открыть anitools в текущей папке из консоли или адресной строки проводника"; GroupDescription: "Дополнительно:"
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Anitools.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\anitools"; Filename: "{app}\Anitools.exe"
Name: "{autodesktop}\anitools"; Filename: "{app}\Anitools.exe"; Tasks: desktopicon

[InstallDelete]
; прежняя ссылка указывает на старый exe — создаётся заново после установки
Type: files; Name: "{app}\ani.exe"

[UninstallDelete]
Type: files; Name: "{app}\ani.exe"

[Run]
; После тихого обновления из программы anitools тоже открывается снова; /nolaunch=1 — не открывать (проверка в CI)
Filename: "{app}\Anitools.exe"; Description: "Открыть anitools"; Flags: nowait postinstall; Check: ShouldLaunch

[Code]
const
  EnvironmentKey = 'Environment';

function CreateHardLink(lpFileName, lpExistingFileName: String; lpSecurityAttributes: Integer): Boolean;
  external 'CreateHardLinkW@kernel32.dll stdcall';

function ShouldLaunch: Boolean;
begin
  Result := ExpandConstant('{param:nolaunch|0}') <> '1';
end;

{ Есть ли папка в списке PATH (без учёта регистра и хвостовой косой) }
function PathHas(Paths, Dir: String): Boolean;
begin
  Result := Pos(';' + Uppercase(RemoveBackslashUnlessRoot(Dir)) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath(Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Paths) then
    Paths := '';
  if PathHas(Paths, Dir) then
    Exit;
  { В начало пользовательского PATH: свой ani.exe важнее чужих одноимённых команд пользователя }
  if Paths = '' then
    Paths := Dir
  else
    Paths := Dir + ';' + Paths;
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Paths);
end;

procedure RemoveFromPath(Dir: String);
var
  Paths, Upper, Needle: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Paths) then
    Exit;
  Paths := ';' + Paths + ';';
  Needle := ';' + Uppercase(RemoveBackslashUnlessRoot(Dir)) + ';';
  Upper := Uppercase(Paths);
  P := Pos(Needle, Upper);
  if P = 0 then
    Exit;
  Delete(Paths, P, Length(Needle) - 1);
  { снять ';' по краям, которые добавили выше }
  while (Length(Paths) > 0) and (Paths[1] = ';') do
    Delete(Paths, 1, 1);
  while (Length(Paths) > 0) and (Paths[Length(Paths)] = ';') do
    Delete(Paths, Length(Paths), 1);
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  App, Ani: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  App := ExpandConstant('{app}');
  Ani := App + '\ani.exe';
  if WizardIsTaskSelected('anicommand') then
  begin
    AddToPath(App);
    DeleteFile(Ani);
    { жёсткая ссылка не занимает места; не вышло (необычная файловая система) — копия }
    if not CreateHardLink(Ani, App + '\Anitools.exe', 0) then
      FileCopy(App + '\Anitools.exe', Ani, False);
  end
  else
    RemoveFromPath(App);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
