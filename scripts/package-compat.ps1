#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$X64Report,
    [Parameter(Mandatory)][string]$X86Report,
    [string]$Compiler,
    [string]$FrameworkInstaller
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\TxtReader.WindowsLegacy\TxtReader.WindowsLegacy.csproj') -Raw
$version = $project.Project.PropertyGroup.InformationalVersion
$dist = Join-Path $root 'dist'
$reports = @{}
foreach ($pair in @(@('x64',$X64Report), @('x86',$X86Report))) {
    $reportPath = (Resolve-Path -LiteralPath $pair[1]).Path
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (!$report.passed -or @($report.checks).Count -lt 1000 -or $report.framework -notlike '.NET Framework*') {
        throw "A passing .NET Framework reading report is required: $reportPath"
    }
    $reports[$pair[0]] = @{path=$reportPath; checks=@($report.checks).Count; framework=$report.framework; os=$report.os}
}
if (!$Compiler) { $Compiler = Join-Path $root '.tools\inno-setup-7\ISCC.exe' }
if (!(Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 7 or pass -Compiler.' }
if (!$FrameworkInstaller) { $FrameworkInstaller = Join-Path $root '.tools\framework48\NDP48-x86-x64-AllOS-ENU.exe' }
$frameworkUrl = 'https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe'
$frameworkHash = '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'
if (!(Test-Path -LiteralPath $FrameworkInstaller)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $FrameworkInstaller) -Force | Out-Null
    Invoke-WebRequest -Uri $frameworkUrl -OutFile $FrameworkInstaller
}
$FrameworkInstaller = (Resolve-Path -LiteralPath $FrameworkInstaller).Path
$signature = Get-AuthenticodeSignature -LiteralPath $FrameworkInstaller
if ((Split-Path -Leaf $FrameworkInstaller) -ne 'NDP48-x86-x64-AllOS-ENU.exe' -or
    (Get-FileHash -LiteralPath $FrameworkInstaller -Algorithm SHA256).Hash -ne $frameworkHash -or
    $signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') {
    throw 'The Microsoft redistributable must match the pinned SHA-256 and have a valid Microsoft signature.'
}
$installer = Join-Path $dist "Qidu-$version-Compatibility-Setup.exe"
$checksumFile = Join-Path $dist "SHA256-$version-compat.txt"
foreach ($path in @($installer, $checksumFile, (Join-Path $dist "Qidu-$version-compat-x64.zip"), (Join-Path $dist "Qidu-$version-compat-x86.zip"))) {
    if (Test-Path -LiteralPath $path) { throw "Preserve the existing artifact: $path" }
}
$payloads = @()
foreach ($arch in @('x64','x86')) {
    $folder = Join-Path $dist "Qidu-$version-compat-$arch"
    $exe = Join-Path $folder 'TxtReader.exe'
    if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0] -ne $version) { throw "Wrong app version: $exe" }
    if (!(Test-Path -LiteralPath (Join-Path $folder 'LICENSE.txt'))) { throw "License missing: $folder" }
    $files = @(Get-ChildItem -LiteralPath $folder -Recurse -File)
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($folder, $file.FullName).Replace('\','/')
        if ($relative -match '(^|/)(state|books|artifacts|\.git)/|\.db($|-)|\.(pfx|p12|key|epub)$' -or
            ($relative -match '\.txt$' -and $relative -notmatch '^(Quickstart\.txt|LICENSE\.txt|samples/sample-novel\.txt|third-party/[^/]+\.txt)$')) {
            throw "Private or unrecognized content: $relative"
        }
    }
    $zip = Join-Path $dist "Qidu-$version-compat-$arch.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $payloads += [pscustomobject]@{architecture=$arch; folder=$folder; files=$files.Count; exeSha256=(Get-FileHash -LiteralPath $exe).Hash}
}
& $Compiler "/DAppVersion=$version" "/DPublishRoot=$dist" "/DPackageDir=$dist" "/DFrameworkInstaller=$FrameworkInstaller" (Join-Path $root 'installer\QiduCompatibility.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$assets = @($installer, (Join-Path $dist "Qidu-$version-compat-x64.zip"), (Join-Path $dist "Qidu-$version-compat-x86.zip")) | ForEach-Object {
    [pscustomobject]@{name=(Split-Path -Leaf $_); path=$_; bytes=(Get-Item -LiteralPath $_).Length; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
}
$assets | ForEach-Object { "$($_.sha256)  $($_.name)" } | Set-Content -LiteralPath $checksumFile -Encoding ascii
$receipt = Join-Path $root "artifacts\compat-$version-package.json"
[pscustomobject]@{version=$version; preview=$true; olderWindowsVerified=$false; reports=$reports; payloads=$payloads; assets=$assets; frameworkUrl=$frameworkUrl; frameworkSha256=$frameworkHash} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receipt -Encoding utf8
Write-Output "Package receipt: $receipt"
$assets | Format-Table name,bytes,sha256 -AutoSize
