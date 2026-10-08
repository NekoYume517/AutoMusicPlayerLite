#ifndef PublishDir
  #error PublishDir must be supplied
#endif
#ifndef OutputDir
  #error OutputDir must be supplied
#endif
[Setup]
AppId={{ED9CEAF1-E7B5-4D87-B095-1E4C7E215E03}
AppName=Auto Music Player Lite
AppVersion=2.1.0
AppVerName=Auto Music Player Lite 2.1.0
AppPublisher=NekoYume517
AppPublisherURL=https://github.com/NekoYume517/AutoMusicPlayerLite
DefaultDirName={localappdata}\Programs\AutoMusicPlayerLitePublic
DefaultGroupName=Auto Music Player Lite
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=AutoMusicPlayerLite-2.1.0-Setup-x64
SetupIconFile={#PublishDir}\app.ico
UninstallDisplayIcon={app}\AutoMusicPlayerLite.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#PublishDir}\LICENSE
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion=2.1.0.0
VersionInfoDescription=Auto Music Player Lite native WinUI 3 installer

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Auto Music Player Lite"; Filename: "{app}\AutoMusicPlayerLite.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Auto Music Player Lite"; Filename: "{app}\AutoMusicPlayerLite.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AutoMusicPlayerLite.exe"; Description: "Launch Auto Music Player Lite"; Flags: nowait postinstall skipifsilent
