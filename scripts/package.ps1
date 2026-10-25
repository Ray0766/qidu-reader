#Requires -Version 7.0
param([string]$Version, [Parameter(Mandatory)][string[]]$ValidationReports)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\TxtReader.App\TxtReader.App.csproj') -Raw
    $Version = $project.Project.PropertyGroup.Version
}
$folder = Join-Path $root "dist\TxtReader-$Version-win-x64"
$zipPath = "$folder.zip"
if (Test-Path -LiteralPath $zipPath) { throw 'Preserve the existing package; use a new version.' }
$exe = Join-Path $folder 'TxtReader.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Publish the self-contained app first.' }
if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0] -ne $Version) { throw 'Publishing folder version does not match.' }
if (!(Test-Path -LiteralPath (Join-Path $folder 'LICENSE.txt'))) { throw 'The publishing folder must include the project license.' }
$reports = @()
foreach ($report in $ValidationReports) {
    $reportPath = if ([IO.Path]::IsPathRooted($report)) { $report } else { Join-Path $root $report }
    $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (!$result.passed -or !@($result.checks).Count -or @($result.checks | Where-Object { !$_.passed }).Count) { throw "Unsuccessful validation: $report" }
    $reports += [pscustomobject]@{ path=$report; checks=@($result.checks).Count; sha256=(Get-FileHash -LiteralPath $reportPath).Hash }
}
if (!$reports.Count) { throw 'Supply the actual successful validation report(s); DPI coverage must be documented separately.' }
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts') -Force | Out-Null
$files = @(Get-ChildItem -LiteralPath $folder -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path=[IO.Path]::GetRelativePath($folder, $_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$privateFiles = @($files | Where-Object {
    $_.path -match '(^|/)(state|books|artifacts|\.git)/|\.db($|-)|\.(pfx|p12|key)$' -or
        ($_.path -match '\.txt$' -and $_.path -notmatch '^(Quickstart\.txt|LICENSE\.txt|samples/sample-novel\.txt|third-party/[^/]+\.txt)$')
})
if ($privateFiles.Count) { throw 'Private data found in the publishing folder.' }
[IO.Compression.ZipFile]::CreateFromDirectory($folder, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($archive.Entries | Where-Object { $_.Name })
    if ($entries.Count -ne $files.Count) { throw 'Package file count does not match.' }
    $prefix = [IO.Path]::GetFileName($folder) + '/'
    foreach ($file in $files) {
        $entry = $archive.GetEntry($prefix + $file.path)
        if (!$entry -or $entry.Length -ne $file.bytes) { throw "Missing or truncated: $($file.path)" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $file.sha256) { throw "Package hash mismatch: $($file.path)" }
    }
} finally { $archive.Dispose() }
$manifest = [ordered]@{ version=$Version; package=$zipPath; bytes=(Get-Item -LiteralPath $zipPath).Length; sha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash; verifiedFiles=$files.Count; privateEntries=0; validation=$reports; files=$files }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root "artifacts\package-v$($Version.Replace('.',''))-manifest.json") -Encoding utf8
[pscustomobject]$manifest | Select-Object version,package,bytes,sha256,verifiedFiles,privateEntries | Format-List
