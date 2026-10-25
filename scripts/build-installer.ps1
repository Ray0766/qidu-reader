#Requires -Version 7.0
param([string]$Version, [string]$Compiler)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\TxtReader.App\TxtReader.App.csproj') -Raw
    $Version = $project.Project.PropertyGroup.Version
}
if (!$Compiler) { $Compiler = Join-Path $root '.tools\inno-setup-7\ISCC.exe' }
if (!(Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 7, or pass -Compiler with the path to ISCC.exe.' }
$publish = Join-Path $root "dist\TxtReader-$Version-win-x64"
$exe = Join-Path $publish 'TxtReader.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Publish the self-contained app first: scripts/build.ps1 -Publish' }
$publishedVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0]
if ($publishedVersion -ne $Version) { throw "Publish version $publishedVersion does not match requested $Version." }
if (!(Test-Path -LiteralPath (Join-Path $publish 'LICENSE.txt'))) { throw 'The publishing folder must include the project license.' }
$privateFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object {
    $relative = [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\','/')
    $relative -match '(^|/)(state|books|artifacts|\.git)/|\.db($|-)|\.(pfx|p12|key)$' -or
        ($relative -match '\.txt$' -and $relative -notmatch '^(Quickstart\.txt|LICENSE\.txt|samples/sample-novel\.txt|third-party/[^/]+\.txt)$')
})
if ($privateFiles.Count) { throw 'Private files found in the publishing folder.' }
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts') -Force | Out-Null
$installer = Join-Path $root "dist\TXT-Reader-$Version-Setup-x64.exe"
if (Test-Path -LiteralPath $installer) { throw 'Preserve the existing installer; move the unshipped candidate to artifacts or use a new version.' }
& $Compiler "/DAppVersion=$Version" "/DPublishDir=$publish" "/DPackageDir=$(Join-Path $root 'dist')" (Join-Path $root 'installer\TxtReader.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
[pscustomobject]@{ Version=$Version; Installer=$installer; Bytes=(Get-Item -LiteralPath $installer).Length;
    SHA256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root "artifacts\installer-v$($Version.Replace('.',''))-manifest.json") -Encoding utf8
Get-Content -LiteralPath (Join-Path $root "artifacts\installer-v$($Version.Replace('.',''))-manifest.json")
