#define MyAppName "MuseBox"
#define MyAppVersion "1.1.21"
#define MyAppPublisher "MuseBox"
#define MyAppExeName "MuseBox.exe"

[Setup]
AppId={{8B8D8974-38D3-45EA-A851-00E8ADAA67F1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\MuseBox
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish\installer
OutputBaseFilename=MuseBox-{#MyAppVersion}
; Use the built-in setup icon: Windows resource updates of a replacement loader
; icon fail with error 110 on this build host. The installed app retains its icon.
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\publish\v{#MyAppVersion}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\InspirationCollector.exe"
Type: files; Name: "{group}\灵感收集器.lnk"
Type: files; Name: "{userdesktop}\灵感收集器.lnk"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他选项："; Flags: unchecked
Name: "scenes"; Description: "关联 .mubo 场景文件（支持双击打开）"; GroupDescription: "其他选项："

[Registry]
Root: HKCU; Subkey: "Software\Classes\.mubo"; ValueType: string; ValueName: ""; ValueData: "MuseBox.Scene"; Flags: uninsdeletevalue; Tasks: scenes
Root: HKCU; Subkey: "Software\Classes\.mubo\OpenWithProgids"; ValueType: string; ValueName: "MuseBox.Scene"; ValueData: ""; Flags: uninsdeletevalue; Tasks: scenes
Root: HKCU; Subkey: "Software\Classes\MuseBox.Scene"; ValueType: string; ValueName: ""; ValueData: "MuseBox 场景"; Flags: uninsdeletekey; Tasks: scenes
Root: HKCU; Subkey: "Software\Classes\MuseBox.Scene\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Assets\scene-icon.ico"",0"; Tasks: scenes
Root: HKCU; Subkey: "Software\Classes\MuseBox.Scene\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: scenes

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
