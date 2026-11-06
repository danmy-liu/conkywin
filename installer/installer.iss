; ConkyWin 安装脚本（Inno Setup 6.5+，简体中文向导）
;
; 由 build-release.ps1 调用：
;   ISCC.exe /DPkgVer=1.1.0 /DPkgDir=ConkyWin-1.1.0-win-x64 installer\installer.iss
; 也可以直接双击本文件用 Inno Setup 编译器打开，会用下面的默认值。
; 注意：宏名不能用 Ver —— 会覆盖 ISPP 内置的 VER（编译器版本常量）。
; ChineseSimplified.isl 取自 Inno Setup 官方仓库 jrsoftware/issrc 的简体中文翻译
; （Kira 维护），随仓库分发，保证本机与 CI 都能编译出全中文向导，不依赖镜像预装的语言包。
;
; 设计要点：
; 1. DisableDirPage=no —— 用户可以自由选择安装位置。
; 2. 安装到 Program Files 时普通权限写不了 config.json，所以程序检测 installed.flag，
;    把用户可改的文件放 %LOCALAPPDATA%\ConkyWin，升级安装不会覆盖用户配置。
; 3. 桌面 / 开始菜单快捷方式、开机自启、安装后启动都在「选择附加任务」页可勾选。

#ifndef PkgVer
  #define PkgVer "1.1.0"
#endif
#ifndef PkgDir
  #define PkgDir "ConkyWin-1.0.0-win-x64"
#endif

#define MyAppName "ConkyWin"
#define MyAppPublisher "ConkyWin Contributors"
#define MyAppURL "https://github.com/danmy-liu/conkywin"

[Setup]
AppId={{8F3D2B71-5C4E-4A9D-B6E1-7A0C9D4E2F18}
AppName={#MyAppName}
AppVersion={#PkgVer}
AppVerName={#MyAppName} {#PkgVer}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppComments=Conky 风格的 Windows 桌面系统监控挂件
UninstallDisplayName=ConkyWin 桌面监控挂件
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=ConkyWin-{#PkgVer}-setup
SetupIconFile=..\assets\conkywin.ico
UninstallDisplayIcon={app}\ConkyWin.exe
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 默认仅当前用户安装（不需要管理员权限）；向导上仍可切换成「为所有用户安装」
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 安装时若 ConkyWin 正在运行，先请 Windows 尝试优雅关闭，避免 exe 被占用
CloseApplications=yes
CloseApplicationsFilter=ConkyWin.exe
VersionInfoVersion={#PkgVer}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=ConkyWin 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#PkgVer}.0

[Languages]
Name: "cn"; MessagesFile: "ChineseSimplified.isl"

[Messages]
cn.FinishedLabel=安装程序已在您的计算机中安装了 ConkyWin。%n%n桌面面板是透明挂件，不会弹出普通窗口；请查看任务栏托盘区的 ConkyWin 图标，双击即可打开设置窗口，也可以从「开始」菜单启动。%n%n配置文件位于 %LOCALAPPDATA%\ConkyWin，卸载时会询问是否保留。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenu"; Description: "创建「开始」菜单快捷方式"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "开机自动启动 ConkyWin（之后可在托盘菜单里关闭）"; GroupDescription: "启动选项："
Name: "launch"; Description: "安装完成后立即运行 ConkyWin"; GroupDescription: "启动选项："

[Files]
; --- 程序本体（只读，升级时直接覆盖）---
Source: "..\dist\{#PkgDir}\ConkyWin.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\{#PkgDir}\assets\*"; DestDir: "{app}\assets"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\dist\{#PkgDir}\config.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\{#PkgDir}\script.lua"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\{#PkgDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\{#PkgDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
; 安装版标记：程序看到它就改用 %LOCALAPPDATA%\ConkyWin 作为可写数据目录
Source: "installed.flag"; DestDir: "{app}"; Flags: ignoreversion

; --- 用户数据目录：只在第一次安装时放默认配置，升级绝不覆盖，卸载默认保留 ---
Source: "..\dist\{#PkgDir}\config.json"; DestDir: "{localappdata}\ConkyWin"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall
Source: "..\dist\{#PkgDir}\script.lua"; DestDir: "{localappdata}\ConkyWin"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall

[Icons]
; 桌面图标：一个启动桌面挂件，一个直接打开设置窗口（挂件本身透明、入口在托盘）
Name: "{autodesktop}\ConkyWin"; Filename: "{app}\ConkyWin.exe"; WorkingDir: "{app}"; IconFilename: "{app}\ConkyWin.exe"; IconIndex: 0; Tasks: desktopicon
Name: "{autodesktop}\ConkyWin 设置"; Filename: "{app}\ConkyWin.exe"; Parameters: "--settings"; WorkingDir: "{app}"; IconFilename: "{app}\ConkyWin.exe"; IconIndex: 0; Tasks: desktopicon
Name: "{autoprograms}\ConkyWin\ConkyWin"; Filename: "{app}\ConkyWin.exe"; WorkingDir: "{app}"; IconFilename: "{app}\ConkyWin.exe"; Tasks: startmenu
Name: "{autoprograms}\ConkyWin\ConkyWin 设置"; Filename: "{app}\ConkyWin.exe"; Parameters: "--settings"; WorkingDir: "{app}"; IconFilename: "{app}\ConkyWin.exe"; Tasks: startmenu
Name: "{autoprograms}\ConkyWin\配置文件夹"; Filename: "{localappdata}\ConkyWin"; WorkingDir: "{localappdata}\ConkyWin"; Tasks: startmenu
Name: "{autoprograms}\ConkyWin\卸载 ConkyWin"; Filename: "{uninstallexe}"; Tasks: startmenu

[Registry]
; 与托盘菜单「开机自启」共用同一个 HKCU\Run 值，勾选状态两边一致
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ConkyWin"; ValueData: """{app}\ConkyWin.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\ConkyWin.exe"; Description: "{cm:LaunchProgram,ConkyWin}"; Flags: nowait postinstall skipifsilent; Tasks: launch

[Code]
// 安装前若默认目录已有旧版本，先提醒一次（Inno 会保留用户数据目录里的配置）
function InitializeSetup(): Boolean;
var
  Old: String;
begin
  Result := True;
  Old := ExpandConstant('{autopf}\{#MyAppName}\ConkyWin.exe');
  if FileExists(Old) then
    Result := SuppressibleMsgBox('检测到已安装的 ConkyWin。继续安装会升级程序文件，你在设置窗口里改过的配置与脚本会保留。' + #13#10#13#10 + '是否继续？',
      mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;

// 卸载最后一步：让用户决定要不要连个人配置一起删掉（默认保留）
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\ConkyWin');
    if DirExists(DataDir) then
      if SuppressibleMsgBox('是否同时删除 ConkyWin 的个人配置、Lua 脚本和日志？' + #13#10 + DataDir + #13#10#13#10 + '选「否」会保留，方便以后重装继续使用。',
        mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
