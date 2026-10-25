param(
    [string]$InputFolder = 'C:\QiduTest',
    [string]$OutputFolder = 'C:\QiduResults'
)
$ErrorActionPreference = 'Stop'
# This entry point is deliberately limited to the disposable Windows Sandbox account.
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $InputFolder -ne 'C:\QiduTest' -or $OutputFolder -ne 'C:\QiduResults') {
    throw 'Run this script only through the prepared Windows Sandbox configuration.'
}
$runId = [Guid]::NewGuid().ToString('N')
$runFolder = Join-Path $OutputFolder $runId
$probeFolder = Join-Path $runFolder 'probes'
New-Item -ItemType Directory -Path $probeFolder -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$failure = $null
function Save-Json($Value, [string]$Name) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $runFolder $Name) -Encoding UTF8
}
function Check([bool]$Condition, [string]$Name, $Detail = $null) {
    $checks.Add([ordered]@{ name = $Name; passed = $Condition; detail = $Detail })
    if (!$Condition) { throw $Name }
}
function Get-BooksDigest([string]$Library) {
    $bookFolder = Join-Path $Library 'books'
    return @((Get-ChildItem -LiteralPath $bookFolder -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($bookFolder.Length); hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })) | ConvertTo-Json -Depth 5 -Compress
}
function Get-StableTables($Tables) {
    # Only the last-read timestamp is expected to change when a book is reopened.
    $sessions = @($Tables.sessions | ForEach-Object { ,@($_ | Select-Object -First 6) })
    return [ordered]@{ books = $Tables.books; sessions = $sessions; bookmarks = $Tables.bookmarks; settings = $Tables.settings } |
        ConvertTo-Json -Depth 20 -Compress
}
function Invoke-Probe([string]$Stage, [bool]$Advance, [string]$Version) {
    $configPath = Join-Path $runFolder ($Stage + '-config.json')
    [ordered]@{ RunId = $runId; Stage = $Stage; Advance = $Advance; ReportFolder = $probeFolder } |
        ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $InputFolder 'invoke-msix-probe.ps1') -Config $configPath *>&1 |
        Out-File -LiteralPath (Join-Path $runFolder ($Stage + '-activation.txt')) -Encoding UTF8
    Check ($LASTEXITCODE -eq 0) ($Stage + ': activation and probe exit successfully')
    $report = Get-Content -LiteralPath (Join-Path $probeFolder ($Stage + '.json')) -Raw | ConvertFrom-Json
    Check ([bool]$report.passed) ($Stage + ': reading checks passed') $report.failure
    Check ($report.package -eq ('Qidu.InstallValidation_' + $Version + '_x64__c262nsasssf58')) ($Stage + ': expected installed version') $report.package
    Check ($report.executable -like '*\WindowsApps\Qidu.InstallValidation_*\TxtReader.exe') ($Stage + ': executable is the installed MSIX') $report.executable
    return $report
}
Start-Transcript -LiteralPath (Join-Path $runFolder 'transcript.txt') | Out-Null
[ordered]@{ runId = $runId; state = 'running'; output = $runFolder } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $OutputFolder 'latest.json') -Encoding UTF8
try {
    $manifest = Get-Content -LiteralPath (Join-Path $InputFolder 'input-manifest.json') -Raw | ConvertFrom-Json
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $admin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $netInterfaces = @(Get-NetAdapter -ErrorAction SilentlyContinue | Select-Object Name, Status, InterfaceDescription)
    Save-Json ([ordered]@{ username = $env:USERNAME; os = (Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber); isAdministrator = $admin; dotnetCommand = [string]$dotnetCommand.Source; networkInterfaces = $netInterfaces; input = $manifest }) 'environment.json'
    Check $admin 'Sandbox account can install the isolated unsigned test package'
    Check ($null -eq $dotnetCommand) 'No machine-wide dotnet command or SDK is installed'
    Check (@($netInterfaces | Where-Object Status -eq 'Up').Count -eq 0) 'Sandbox has no active network adapter'
    Check (@(Get-AppxPackage -Name 'Qidu.InstallValidation').Count -eq 0) 'Clean system has no previous validation package'
    foreach ($file in $manifest.files) {
        $path = Join-Path $InputFolder $file.name
        Check ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $file.sha256) ('Input hash: ' + $file.name)
    }
    Add-AppxPackage -Path (Join-Path $InputFolder 'Qidu-LOCAL-TEST-1.0.9.0-x64.msix') -AllowUnsigned
    $first = Invoke-Probe 'first' $true '1.0.9.0'
    Check (@($first.before.books).Count -eq 0 -and @($first.before.sessions).Count -eq 0) 'Fresh install starts with an empty library'
    Check (@($first.after.books).Count -eq 2 -and @($first.after.bookmarks).Count -eq 1) 'Fresh install imports two books and saves a bookmark'
    $baseline = Get-StableTables $first.after
    $bookHashes = Get-BooksDigest $first.library
    $restart = Invoke-Probe 'restart' $false '1.0.9.0'
    Check ((Get-StableTables $restart.before) -eq $baseline -and (Get-StableTables $restart.after) -eq $baseline) 'Restart preserves exact anchors, shelf, bookmarks and preferences'
    Add-AppxPackage -Path (Join-Path $InputFolder 'Qidu-LOCAL-TEST-1.0.10.0-x64.msix') -AllowUnsigned
    $upgrade = Invoke-Probe 'upgrade' $false '1.0.10.0'
    Check ((Get-StableTables $upgrade.before) -eq $baseline -and (Get-StableTables $upgrade.after) -eq $baseline) 'Upgrade preserves exact anchors, shelf, bookmarks and preferences'
    $installed = Get-AppxPackage -Name 'Qidu.InstallValidation'
    Check ($installed.PackageFullName -eq 'Qidu.InstallValidation_1.0.10.0_x64__c262nsasssf58') 'Only the expected test package will be removed'
    Remove-AppxPackage -Package $installed.PackageFullName
    Check (@(Get-AppxPackage -Name 'Qidu.InstallValidation').Count -eq 0) 'Windows confirms test package uninstalled'
    Check (Test-Path -LiteralPath $first.library) 'Independent library survives uninstall'
    Check ((Get-BooksDigest $first.library) -eq $bookHashes) 'Book hashes survive uninstall'
    Save-Json ([ordered]@{ packages = @(Get-AppxPackage -Name 'Qidu.InstallValidation'); bookHashes = $bookHashes }) 'removed.json'
    Add-AppxPackage -Path (Join-Path $InputFolder 'Qidu-LOCAL-TEST-1.0.10.0-x64.msix') -AllowUnsigned
    $reinstall = Invoke-Probe 'reinstall' $false '1.0.10.0'
    Check ((Get-StableTables $reinstall.before) -eq $baseline -and (Get-StableTables $reinstall.after) -eq $baseline) 'Reinstall preserves exact anchors, shelf, bookmarks and preferences'
    Check ((Get-BooksDigest $reinstall.library) -eq $bookHashes) 'All imported book hashes remain unchanged'
    Save-Json ([ordered]@{ bookHashes = $bookHashes; stableTables = $baseline }) 'baseline.json'
} catch {
    $failure = $_.Exception.ToString()
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $runFolder 'failure.txt') -Encoding UTF8
} finally {
    Save-Json ([ordered]@{ passed = ($null -eq $failure); runId = $runId; checks = @($checks.ToArray()); failure = $failure; scope = 'Clean offline Windows Sandbox; isolated unsigned MSIX identity with the final 0.1.9 payload. Not a Microsoft Store download test.' }) 'summary.json'
    [ordered]@{ runId = $runId; state = $(if ($failure) { 'failed' } else { 'passed' }); output = $runFolder } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $OutputFolder 'latest.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}
if ($failure) { exit 1 }
