; A separate preview installation shares the stable library, never its binaries.
#ifndef AppVersion
  #define AppVersion "0.2.0-preview.2"
#endif
#ifndef PublishRoot
  #define PublishRoot "..\dist"
#endif
#ifndef PackageDir
  #define PackageDir "..\dist"
#endif
#ifndef FrameworkInstaller
  #define FrameworkInstaller "..\.tools\framework48\NDP48-x86-x64-AllOS-ENU.exe"
#endif

[Setup]
#ifdef ValidationBuild
AppId={{A925002F-1351-48DC-A080-00BC247BF704}
AppName=栖读 · 兼容安装验证
DefaultDirName={localappdata}\Programs\Qidu Compatibility Validation
DefaultGroupName=栖读 · 兼容安装验证
OutputBaseFilename=Qidu-{#AppVersion}-Compatibility-Validation-Setup
AppMutex=Local\Qidu.Compatibility.Validation.Running
#else
AppId={{24DC9887-C308-4251-A12D-5BE208CF1D3F}
AppName=栖读 · 兼容预览
DefaultDirName={localappdata}\Programs\Qidu Compatibility Preview
DefaultGroupName=栖读 · 兼容预览
OutputBaseFilename=Qidu-{#AppVersion}-Compatibility-Setup
AppMutex=Local\TXTReader.Desktop.Running
#endif
AppVersion={#AppVersion}
AppPublisher=Ray0766
AppPublisherURL=https://github.com/Ray0766/qidu-reader
PrivilegesRequired=lowest
ArchitecturesAllowed=x86compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible and not arm64
MinVersion=6.1sp1
WizardStyle=modern
SetupIconFile=..\src\TxtReader.App\Assets\AppIcon.ico
InfoBeforeFile=..\docs\compatibility-preview.txt
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\TxtReader.exe
OutputDir={#PackageDir}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.NeedFramework=这台电脑缺少 Microsoft .NET Framework 4.8。接下来会打开包内的微软官方安装器，请按提示完成管理员授权和许可确认，然后返回继续。
english.NeedFramework=Microsoft .NET Framework 4.8 is required. The bundled Microsoft installer will open. Complete its permission and license prompts to continue.
chinesesimplified.FrameworkCanceled=运行组件尚未安装，栖读尚未安装。请完成 Microsoft .NET Framework 4.8 安装后重试。
english.FrameworkCanceled=The runtime is not installed, and Qidu has not been installed. Install Microsoft .NET Framework 4.8 and retry.
chinesesimplified.FrameworkRestart=运行组件需要重启才能完成。请先保存工作，自行重启电脑后重新打开本安装包。
english.FrameworkRestart=The runtime requires a restart. Save your work, restart Windows yourself, then open this installer again.
chinesesimplified.UnsupportedWindows8=Windows 8 不支持本版所需的运行组件，请先升级至 Windows 8.1 或更新系统。
english.UnsupportedWindows8=Windows 8 is not supported. Upgrade to Windows 8.1 or later first.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishRoot}\Qidu-{#AppVersion}-compat-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: Is64BitInstallMode
Source: "{#PublishRoot}\Qidu-{#AppVersion}-compat-x86\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not Is64BitInstallMode
Source: "{#FrameworkInstaller}"; Flags: dontcopy nocompression

[Icons]
Name: "{group}\栖读 · 兼容预览"; Filename: "{app}\TxtReader.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\栖读 · 兼容预览"; Filename: "{app}\TxtReader.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TxtReader.exe"; Description: "{cm:LaunchProgram,栖读 · 兼容预览}"; Flags: nowait postinstall skipifsilent

[Code]
var RuntimeNeedsRestart: Boolean;

function InitializeSetup: Boolean;
var Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := not ((Version.Major = 6) and (Version.Minor = 2));
  if not Result then
    SuppressibleMsgBox(CustomMessage('UnsupportedWindows8'), mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode: Integer;
begin
  Result := '';
  if RuntimeNeedsRestart then begin
    Result := CustomMessage('FrameworkRestart');
    exit;
  end;
  if IsDotNetInstalled(net48, 0) then exit;
  // Never launch a permission dialog during a silent install. Return a clear failure.
  if WizardSilent then begin
    Result := CustomMessage('FrameworkCanceled');
    exit;
  end;
  SuppressibleMsgBox(CustomMessage('NeedFramework'), mbInformation, MB_OK, IDOK);
  ExtractTemporaryFile('NDP48-x86-x64-AllOS-ENU.exe');
  // Full Microsoft UI owns the license/permission prompts. Never reboot automatically.
  if not ShellExec('runas', ExpandConstant('{tmp}\NDP48-x86-x64-AllOS-ENU.exe'),
      '/norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then begin
    Result := CustomMessage('FrameworkCanceled');
    exit;
  end;
  if (ExitCode = 3010) or (ExitCode = 1641) then begin
    RuntimeNeedsRestart := True;
    Result := CustomMessage('FrameworkRestart');
  end else if (ExitCode <> 0) or not IsDotNetInstalled(net48, 0) then
    Result := CustomMessage('FrameworkCanceled') + ' (' + IntToStr(ExitCode) + ')';
end;

// No library or framework removal entries: uninstall only owns this preview's program files.
