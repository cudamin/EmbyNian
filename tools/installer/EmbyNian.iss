; EmbyNian 的 Inno Setup 安装包脚本。
;
;   ISCC.exe tools\installer\EmbyNian.iss
;   ISCC.exe /DMyAppVersion=0.0.2 /DVersionQuad=0.0.2.0 tools\installer\EmbyNian.iss
;
; 不带 /D 时用的是下面两个默认值，只对 0.0.1 成立；正式出包走 tools\installer.ps1，
; 版本号从 Directory.Build.props 读出来传进来，两处不会各报一个版本。
; 打包对象是 tools\publish.ps1 的产物（artifacts\publish\win-x64，自包含），
; 所以先跑一遍 publish.ps1 再编译本脚本，装的才是刚构建的那一份。

#define MyAppName "EmbyNian"
#ifndef MyAppVersion
#define MyAppVersion "0.0.1"
#endif
#ifndef VersionQuad
#define VersionQuad "0.0.1.0"
#endif
#define PublishRoot "..\..\artifacts\publish\win-x64"

[Setup]
AppId={{7A2C4E90-5B1D-4F3A-9C68-2E5D8B14A9F3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#VersionQuad}
VersionInfoTextVersion={#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={userpf}\{#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts
OutputBaseFilename=EmbyNian_windows-x64_{#MyAppVersion}
SetupIconFile=..\..\app.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\EmbyNian.exe
; 界面只有中文，安装器也就只留中文。
ShowLanguageDialog=no
; 装进当前用户的目录，不弹 UAC；这台程序的 MSIX 同样是按用户装的。
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; README 承诺 Windows 10 1809 起。
MinVersion=10.0.17763
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 升级时程序若正开着，走重启管理器请它退出。
CloseApplications=yes

[Languages]
Name: "chs"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\EmbyNian.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\EmbyNian.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EmbyNian.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent unchecked
