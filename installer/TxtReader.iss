; Keep AppId stable across versions so updates replace the same installation.
#ifndef AppVersion
  #define AppVersion "0.1.9"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist\TxtReader-" + AppVersion + "-win-x64"
#endif
#ifndef PackageDir
  #define PackageDir "..\dist"
#endif

[Setup]
AppId={{6B2CDD5C-D920-4E7D-A2F9-7A6126C58BED}
AppName=栖读
AppVersion={#AppVersion}
AppPublisher=Ray0766
DefaultDirName={localappdata}\Programs\TXT Reader
DefaultGroupName=栖读
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
WizardStyle=modern
SetupIconFile=..\src\TxtReader.App\Assets\AppIcon.ico
DisableProgramGroupPage=yes
DisableReadyPage=yes
UninstallDisplayIcon={app}\TxtReader.exe
OutputDir={#PackageDir}
OutputBaseFilename=TXT-Reader-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
AppMutex=Local\TXTReader.Desktop.Running
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\栖读"; Filename: "{app}\TxtReader.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,栖读}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\栖读"; Filename: "{app}\TxtReader.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TxtReader.exe"; Description: "{cm:LaunchProgram,栖读}"; Flags: nowait postinstall skipifsilent

; Reading data lives outside {app}, under the user's QiduLibrary.
; The application copies the legacy LocalAppData\TxtReaderPreview library on first launch.
; Do not add it to Files, InstallDelete or UninstallDelete: upgrades and
; uninstalls must preserve imported books, progress, bookmarks and preferences.
