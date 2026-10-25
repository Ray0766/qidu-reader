#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Draft')][switch]$Draft,
    [Parameter(Mandatory, ParameterSetName = 'LocalTest')][switch]$LocalTest,
    [Parameter(Mandatory, ParameterSetName = 'Store')][string]$IdentityFile,
    [string]$AppVersion = '0.1.9',
    [string]$PackageVersion = '1.0.9.0',
    [string]$PublishDirectory,
    [string]$MakeAppx
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'AppVersion must be major.minor.patch.' }
if ($PackageVersion -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'Store PackageVersion must have four parts with a final zero.' }
$versionParts = $PackageVersion.Split('.') | ForEach-Object { [int]$_ }
if ($versionParts[0] -lt 1 -or @($versionParts | Where-Object { $_ -gt 65535 }).Count) {
    throw 'Store PackageVersion needs major >= 1 and each part <= 65535.'
}
if ($LocalTest) {
    # Microsoft's Windows 11 unsigned test-package identity; never use this for Store submission.
    $identity = [pscustomobject]@{
        identityName = 'Qidu.InstallValidation'
        publisher = 'CN=Qidu.InstallValidation, OID.2.25.311729368913984317654407730594956997722=1'
        publisherDisplayName = 'Local installation verification'
        displayName = '栖读 · 安装验证'
    }
} elseif ($Draft) {
    $identity = [pscustomobject]@{
        identityName = 'TXTReader.PackagingValidation'
        publisher = 'CN=TXTReader.PackagingValidation'
        publisherDisplayName = 'Packaging validation only'
        displayName = '栖读 (packaging draft)'
    }
} else {
    $identity = Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
    foreach ($name in @('identityName','publisher','publisherDisplayName','displayName')) {
        if (!$identity.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace($identity.$name) -or
            $identity.$name -match 'REPLACE|PackagingValidation|待填写|PLACEHOLDER') {
            throw "Missing real Partner Center identity value: $name. Use -Draft for packaging validation only."
        }
    }
}
if ($identity.identityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$' -or $identity.publisher -notmatch '^CN=') {
    throw 'Copy the exact Name and Publisher (CN=...) from Partner Center Product identity.'
}
if (!$PublishDirectory) { $PublishDirectory = Join-Path $root "dist\TxtReader-$AppVersion-win-x64" }
$source = (Resolve-Path -LiteralPath $PublishDirectory).Path
$sourceItems = @(Get-Item -LiteralPath $source) + @(Get-ChildItem -LiteralPath $source -Recurse -Force)
if (@($sourceItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
    throw 'Publishing folders must not contain reparse points.'
}
$files = @($sourceItems | Where-Object { !$_.PSIsContainer } | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($source, $_.FullName).Replace('\','/')
    if ($relative -match '[^\x20-\x7E]') { throw "Packaged resource filename must use ASCII: $relative" }
    if ($relative -match '(^|/)(state|books|artifacts|\.git)/|\.db($|-)|\.(pfx|p12|key)$' -or
        ($relative -match '\.txt$' -and $relative -notmatch '^(Quickstart\.txt|LICENSE\.txt|samples/sample-novel\.txt|third-party/[^/]+\.txt)$')) {
        throw "Private data or signing key in payload: $relative"
    }
    [pscustomobject]@{ path=$relative; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
foreach ($required in @('TxtReader.exe','TxtReader.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll','third-party/dotnet-runtime-LICENSE.txt')) {
    if ($required -notin $files.path) { throw "Missing self-contained payload: $required" }
}
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'TxtReader.exe')).ProductVersion.Split('+')[0]
if ($actualVersion -ne $AppVersion) { throw "Expected app $AppVersion, found $actualVersion." }
$assetFolder = Join-Path $root 'installer\msix\Assets'
foreach ($asset in @('StoreLogo.png','Square44x44Logo.png','Square150x150Logo.png')) {
    if (!(Test-Path -LiteralPath (Join-Path $assetFolder $asset))) { throw 'Generate MSIX icon assets with IconBuilder --store first.' }
}
if (!$MakeAppx) {
    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $MakeAppx = Get-ChildItem -LiteralPath $sdkBin -Directory | Where-Object { $_.Name -match '^10\.0\.\d+\.0$' } |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$MakeAppx -or !(Test-Path -LiteralPath $MakeAppx)) { throw 'MakeAppx.exe from the Windows SDK is required. Supply -MakeAppx if needed.' }
$sdkSignature = Get-AuthenticodeSignature -LiteralPath $MakeAppx
if ($sdkSignature.Status -ne 'Valid' -or $sdkSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw 'MakeAppx must have a valid Microsoft signature.'
}
$makePri = Join-Path (Split-Path -Parent $MakeAppx) 'makepri.exe'
$priSignature = Get-AuthenticodeSignature -LiteralPath $makePri
if ($priSignature.Status -ne 'Valid' -or $priSignature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw 'MakePri must have a valid Microsoft signature.'
}
$work = Join-Path $root ("artifacts\msix\$AppVersion-" + [guid]::NewGuid().ToString('N').Substring(0,8))
$staging = Join-Path $work 'payload'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
foreach ($file in $files) {
    $destination = Join-Path $staging $file.path
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $file.path) -Destination $destination
}
Copy-Item -LiteralPath $assetFolder -Destination (Join-Path $staging 'StoreAssets') -Recurse
[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'installer\msix\AppxManifest.xml') -Raw
$ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
$ns.AddNamespace('f','http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$ns.AddNamespace('uap','http://schemas.microsoft.com/appx/manifest/uap/windows10')
$manifest.SelectSingleNode('/f:Package/f:Identity', $ns).SetAttribute('Name', $identity.identityName)
$manifest.SelectSingleNode('/f:Package/f:Identity', $ns).SetAttribute('Publisher', $identity.publisher)
$manifest.SelectSingleNode('/f:Package/f:Identity', $ns).SetAttribute('Version', $PackageVersion)
$manifest.SelectSingleNode('/f:Package/f:Properties/f:DisplayName', $ns).InnerText = $identity.displayName
$manifest.SelectSingleNode('/f:Package/f:Properties/f:PublisherDisplayName', $ns).InnerText = $identity.publisherDisplayName
$manifest.SelectSingleNode('//uap:VisualElements', $ns).SetAttribute('DisplayName', $identity.displayName)
$manifest.Save((Join-Path $staging 'AppxManifest.xml'))
$priConfigPath = Join-Path $work 'priconfig.xml'
& $makePri createconfig /cf $priConfigPath /dq lang-zh-CN_scale-100 /pv 10.0.0 *> (Join-Path $work 'makepri-config.log')
if ($LASTEXITCODE -ne 0) { throw "MakePri createconfig failed; see $work\makepri-config.log" }
[xml]$priConfig = Get-Content -LiteralPath $priConfigPath -Raw
# Keep all scales in this single package. Index from the package root so resource
# names preserve the StoreAssets prefix used by the manifest logo paths.
$packaging = $priConfig.SelectSingleNode('/resources/packaging')
if ($packaging) { [void]$packaging.ParentNode.RemoveChild($packaging) }
$priConfig.Save($priConfigPath)
& $makePri new /pr $staging /cf $priConfigPath /mn (Join-Path $staging 'AppxManifest.xml') /of (Join-Path $staging 'resources.pri') *> (Join-Path $work 'makepri.log')
if ($LASTEXITCODE -ne 0) { throw "MakePri new failed; see $work\makepri.log" }
$priDumpPath = Join-Path $work 'resources-dump.xml'
& $makePri dump /if (Join-Path $staging 'resources.pri') /of $priDumpPath *> (Join-Path $work 'makepri-dump.log')
if ($LASTEXITCODE -ne 0) { throw "MakePri dump failed; see $work\makepri-dump.log" }
[xml]$priDump = Get-Content -LiteralPath $priDumpPath -Raw
foreach ($logo in @('Square44x44Logo.png','Square150x150Logo.png','StoreLogo.png')) {
    $resource = $priDump.SelectSingleNode("//ResourceMapSubtree[@name='StoreAssets']/NamedResource[@name='$logo']")
    if (!$resource -or $resource.Candidate.Count -lt 5) { throw "PRI is missing scale resources at StoreAssets/$logo." }
}
$package = if ($LocalTest) { Join-Path $work "Qidu-LOCAL-TEST-$PackageVersion-x64.msix" }
    elseif ($Draft) { Join-Path $work 'TXTReader-DRAFT-x64.msix' }
    else { Join-Path $root "dist\TXT-Reader-$AppVersion-StoreCandidate-x64.msix" }
if (Test-Path -LiteralPath $package) { throw 'Preserve the existing package; use a new AppVersion.' }
$inventory = @(Get-ChildItem -LiteralPath $staging -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path=[IO.Path]::GetRelativePath($staging,$_.FullName).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
& $MakeAppx pack /d $staging /p $package /h SHA256 /v *> (Join-Path $work 'makeappx.log')
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed ($LASTEXITCODE); see $work\makeappx.log" }
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    # MSIX stores OPC part names as escaped URIs (including the Chinese demo name).
    $entries = @{}
    foreach ($entry in $archive.Entries) {
        $name = [Uri]::UnescapeDataString($entry.FullName)
        if ($entries.ContainsKey($name)) { throw "Duplicate decoded package path: $name" }
        $entries[$name] = $entry
    }
    foreach ($file in $inventory) {
        $entry = $entries[$file.path]
        if (!$entry) { throw "Packaged file missing: $($file.path)" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $file.sha256) { throw "Packaged hash differs: $($file.path)" }
    }
    $extra = @($entries.Keys | Where-Object { $_ -notin $inventory.path })
    if (@($extra | Where-Object { $_ -notin @('AppxBlockMap.xml','[Content_Types].xml') }).Count) {
        throw 'Unexpected package-generated entries.'
    }
    if ($archive.GetEntry('AppxSignature.p7x')) { throw 'Unexpected signature in unsigned candidate.' }
} finally { $archive.Dispose() }
$report = [ordered]@{
    passed=$true; kind=$(if ($LocalTest) { 'unsigned-local-install-test' } elseif ($Draft) { 'packaging-draft' } else { 'unsigned-store-candidate' }); appVersion=$AppVersion
    packageVersion=$PackageVersion; identity=$identity; package=$package; bytes=(Get-Item -LiteralPath $package).Length
    sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash; payloadFiles=$files.Count
    verifiedFiles=$inventory.Count; privateEntries=0; makeappx=$MakeAppx; makeappxExitCode=0; makepriExitCode=0
    installed=$false; storeSigned=$false; submitted=$false; migrationVerified=$false
    remaining=@('MSIX install/update/uninstall and progress migration verification','Windows App Certification Kit','Store certification')
    files=$inventory
}
if ($Draft) { $report.remaining = @('Partner Center identity and name reservation') + $report.remaining }
$reportPath = Join-Path $work 'report.json'
$report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $reportPath -Encoding utf8
[pscustomobject]$report | Select-Object kind,package,bytes,sha256,verifiedFiles,installed,storeSigned,migrationVerified | Format-List
Write-Host "Report: $reportPath"
Write-Host 'This unsigned package is for packaging validation / Store submission preparation, not public direct installation.'
