#define AppName "PortableCskin"
#define AppVersion "0.3.0"
#define AppPublisher "Cskin"
#define AppExeName "PortableCskin.exe"
#define PublishDir "..\publish-final"
#ifdef UnsignedTestBuild
  #define InstallerFileName "CskinSetup-UNSIGNED-TEST"
#elif defined LocalTestBuild
  #define InstallerFileName "CskinSetup-LOCAL-SIGNED-TEST"
#else
  #define InstallerFileName "CskinSetup"
#endif

[Setup]
AppId={{7B4E0BB0-8D4D-4E9D-9A4D-7B6C7E0CB1B2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\PortableCskin
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-output
OutputBaseFilename={#InstallerFileName}
Compression=lzma2/normal
SolidCompression=no
WizardStyle=modern
SetupIconFile=..\Assets\cskin.ico
UninstallDisplayIcon={app}\{#AppExeName}
Uninstallable=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=PortableCskin installer
VersionInfoProductName=PortableCskin
VersionInfoCompany={#AppPublisher}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent
