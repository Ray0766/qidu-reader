param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [Parameter(Mandatory)][string]$TestBook,
    [Parameter(Mandatory)][double]$ExpectedDpiScale,
    [ValidateSet('prepared','settling')][string[]]$Phases = @('prepared','settling')
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$TestBook = (Resolve-Path -LiteralPath $TestBook).Path
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if ((Test-Path -LiteralPath $ReportDirectory) -and (Get-ChildItem -LiteralPath $ReportDirectory -Force | Select-Object -First 1)) {
    throw 'Use a new, empty report directory.'
}
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$summary = @()
foreach ($layout in @('single','double')) {
    foreach ($phase in $Phases) {
        foreach ($action in @('deactivate','minimize','close')) {
            $case = Join-Path $ReportDirectory "$layout-$phase-$action"
            $scale = $ExpectedDpiScale.ToString([Globalization.CultureInfo]::InvariantCulture)
            $common = @('--data-dir', ('"' + (Join-Path $case 'state') + '"'), '--expect-dpi', $scale)
            $argsForProbe = @('--desktop-probe', ('"' + $case + '"'), '--probe-action', $action, '--probe-layout', $layout, '--probe-phase', $phase) + $common + @('"' + $TestBook + '"')
            $probe = Start-Process -FilePath $Executable -ArgumentList $argsForProbe -WindowStyle Hidden -PassThru
            if (!$probe.WaitForExit(45000)) { throw "Desktop probe timed out: $case (PID $($probe.Id))" }
            $report = Get-Content -LiteralPath (Join-Path $case 'report.json') -Raw | ConvertFrom-Json
            if (!$report.passed -or $probe.ExitCode -ne 0) { throw "$case : $($report.error)" }
            $resumeArgs = @('--desktop-resume', ('"' + $case + '"')) + $common
            $resume = Start-Process -FilePath $Executable -ArgumentList $resumeArgs -WindowStyle Hidden -PassThru
            if (!$resume.WaitForExit(30000)) { throw "Fresh-process resume timed out: $case" }
            $resumed = Get-Content -LiteralPath (Join-Path $case 'resume-report.json') -Raw | ConvertFrom-Json
            if (!$resumed.passed -or $resume.ExitCode -ne 0) { throw "$case resume: $($resumed.error)" }
            $summary += [pscustomobject]@{ layout=$layout; phase=$phase; action=$action; passed=$true; checks=@($report.checks).Count + @($resumed.checks).Count; dpi=$report.dpi; report=$case }
            $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'summary.json') -Encoding utf8
            Write-Output "$layout / $phase / ${action}: passed, fresh-process resume passed"
        }
    }
}
Write-Output "Desktop cases passed: $($summary.Count)"
