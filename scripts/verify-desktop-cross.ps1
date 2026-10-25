param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$TestEpub
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Use a new report directory.' }
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
$arguments = @('--self-test', $ReportDirectory, '--data-dir', (Join-Path $ReportDirectory 'library'))
if ($TestEpub) { $arguments += @('--test-epub', (Resolve-Path -LiteralPath $TestEpub).Path) }
function Run-Reader([string[]]$ReaderArguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WindowStyle = 'Hidden'
    foreach ($argument in $ReaderArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit(120000)) { throw "Reader checks timed out (PID $($process.Id))." }
    if ($process.ExitCode -ne 0) { throw "Reader checks failed ($($process.ExitCode)); see $ReportDirectory." }
}
Run-Reader $arguments
$report = Get-Content -LiteralPath (Join-Path $ReportDirectory 'report.json') -Raw | ConvertFrom-Json
if (!$report.passed) { throw $report.failure }
Run-Reader @('--resume-test', $ReportDirectory, '--data-dir', (Join-Path $ReportDirectory 'library'))
$resume = Get-Content -LiteralPath (Join-Path $ReportDirectory 'resume-report.json') -Raw | ConvertFrom-Json
if (!$resume.passed) { throw $resume.failure }
Write-Output "Reading checks: $($report.checks.Count); fresh-process checks: $($resume.checks.Count); native Mac: $($report.nativeMacVerified)."
