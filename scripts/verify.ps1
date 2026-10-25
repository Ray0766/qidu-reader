param([string]$Executable, [string]$ReportDirectory, [string]$TestBook, [double]$ExpectedDpiScale, [string]$TestEpub, [switch]$EpubOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$Executable) { $Executable = Join-Path $projectRoot 'src\TxtReader.App\bin\Release\net10.0-windows\TxtReader.exe' }
if (!$ReportDirectory) { $ReportDirectory = Join-Path $projectRoot ('artifacts\verification-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
if (!(Test-Path -LiteralPath $Executable)) { throw 'Build the app first with scripts/build.ps1.' }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if ((Test-Path -LiteralPath $ReportDirectory) -and (Get-ChildItem -LiteralPath $ReportDirectory -Force | Select-Object -First 1)) {
    throw 'Use a new, empty report directory so earlier test fixtures and reports are preserved.'
}
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$testArguments = @('--self-test', ('"' + $ReportDirectory + '"'))
if ($TestBook) { $testArguments += @('--test-book', ('"' + (Resolve-Path -LiteralPath $TestBook).Path + '"')) }
if ($TestEpub) { $testArguments += @('--test-epub', ('"' + (Resolve-Path -LiteralPath $TestEpub).Path + '"')) }
if ($EpubOnly) { $testArguments += '--epub-only' }
if ($ExpectedDpiScale) { $testArguments += @('--expect-dpi', $ExpectedDpiScale.ToString([Globalization.CultureInfo]::InvariantCulture)) }
$testProcess = Start-Process -FilePath $Executable -ArgumentList $testArguments -WindowStyle Hidden -PassThru
$testProcess.WaitForExit()
$reportPath = Join-Path $ReportDirectory 'report.json'
if (!(Test-Path -LiteralPath $reportPath)) { throw "The test process exited without a report. See $ReportDirectory." }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
Write-Output "Passed: $($report.passed); checks: $(@($report.checks).Count); exit: $($testProcess.ExitCode)"
$report.timings | Where-Object { $_.fileMiB } | Select-Object fileMiB,parseMs,firstLayoutMs,processWorkingSetMiB | Format-Table
$report.timings | Where-Object { $_.realBook } | Select-Object realBook,bytes,chapters,openMs,firstBatchSearchMs,latePhraseSearchMs | Format-Table
Write-Output "Report: $reportPath"
if (!$report.passed -or $testProcess.ExitCode -ne 0) { throw $report.failure }
