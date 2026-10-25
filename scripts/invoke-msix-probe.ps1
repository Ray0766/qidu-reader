param([Parameter(Mandatory)][string]$Config)
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$package = Get-AppxPackage -Name 'Qidu.InstallValidation'
if (!$package) { throw 'Install the isolated validation MSIX first.' }
# Windows activation API: uses the registered AUMID, not direct execution of the extracted EXE.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IQiduActivationManager {
    void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string app, [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
    void ActivateForFile(IntPtr app, IntPtr items, IntPtr verb, out uint pid);
    void ActivateForProtocol(IntPtr app, IntPtr items, out uint pid);
}
public static class QiduActivation {
    public static uint Start(string app, string args) {
        var manager = (IQiduActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
        uint pid; manager.ActivateApplication(app, args, 2, out pid); return pid;
    }
}
'@
$appId = $package.PackageFamilyName + '!Reader'
$reportPath = Join-Path $settings.ReportFolder ($settings.Stage + '.json')
if (Test-Path -LiteralPath $reportPath) { throw 'A report already exists; use a fresh probe stage.' }
$probePid = [QiduActivation]::Start($appId, ('--package-probe "' + ([IO.Path]::GetFullPath($Config)) + '"'))
Write-Output "Activated $appId; process ID: $probePid"
$deadline = [DateTime]::UtcNow.AddMinutes(3)
while (!(Test-Path -LiteralPath $reportPath)) {
    $startupError = Join-Path $settings.ReportFolder ($settings.Stage + '-startup-error.json')
    if (Test-Path -LiteralPath $startupError) { throw (Get-Content -LiteralPath $startupError -Raw) }
    if ([DateTime]::UtcNow -gt $deadline) { throw "Package probe timed out: $probePid" }
    Start-Sleep -Milliseconds 300
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$process = Get-Process -Id $probePid -ErrorAction SilentlyContinue
if ($process -and !$process.WaitForExit(15000)) { throw 'Probe did not close after saving.' }
$report | Select-Object passed,package,stage,processId,library,failure | ConvertTo-Json
if (!$report.passed) { throw $report.failure }
