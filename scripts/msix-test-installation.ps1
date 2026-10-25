# Run elevated only after the user accepts Windows UAC. Never imports certificates or changes security settings.
param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
$testPlan = Get-Content -LiteralPath $Plan -Raw | ConvertFrom-Json
$testFolder = Split-Path -Parent ([IO.Path]::GetFullPath($Plan))
$testName = 'Qidu.InstallValidation'
function Write-Receipt([string]$Step) {
    $installed = @(Get-AppxPackage -AllUsers -Name $testName | Select-Object Name,Version,PackageFullName,PackageFamilyName,InstallLocation)
    @{ step=$Step; at=[DateTimeOffset]::Now.ToString('O'); packages=$installed } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testFolder "$Step.json") -Encoding UTF8
}
function Wait-Signal([string]$Step) {
    $deadline = [DateTime]::UtcNow.AddMinutes(25)
    while (!(Test-Path -LiteralPath (Join-Path $testFolder "$Step.signal"))) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out waiting for $Step." }
        if (Test-Path -LiteralPath (Join-Path $testFolder 'cancel.signal')) { throw 'Test canceled.' }
        Start-Sleep -Seconds 1
    }
}
function Assert-Package($Item) {
    if ((Get-FileHash -LiteralPath $Item.path -Algorithm SHA256).Hash -ne $Item.sha256) { throw 'Test package hash changed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Item.path)
    try {
        $stream = [IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
        try { [xml]$manifest = $stream.ReadToEnd() } finally { $stream.Dispose() }
        if ($manifest.Package.Identity.Name -ne $testName -or $manifest.Package.Identity.Publisher -ne 'CN=Qidu.InstallValidation, OID.2.25.311729368913984317654407730594956997722=1') {
            throw 'Only the isolated unsigned test identity may be installed by this script.'
        }
    } finally { $zip.Dispose() }
}
try {
    Assert-Package $testPlan.first
    Assert-Package $testPlan.upgrade
    $current = @(Get-AppxPackage -AllUsers -Name $testName)
    if ($current.Count -and ($current.Count -ne 1 -or !$testPlan.previousVersion -or $current[0].Version.ToString() -ne $testPlan.previousVersion)) {
        throw 'Existing validation version does not match the reviewed plan.'
    }
    Add-AppxPackage -Path $testPlan.first.path -AllowUnsigned
    Write-Receipt 'installed'
    Wait-Signal 'upgrade'
    Add-AppxPackage -Path $testPlan.upgrade.path -AllowUnsigned
    Write-Receipt 'upgraded'
    Wait-Signal 'remove'
    Get-AppxPackage -AllUsers -Name $testName | Remove-AppxPackage -AllUsers
    Write-Receipt 'removed'
    Wait-Signal 'reinstall'
    Add-AppxPackage -Path $testPlan.upgrade.path -AllowUnsigned
    Write-Receipt 'reinstalled'
    Wait-Signal 'cleanup'
    Get-AppxPackage -AllUsers -Name $testName | Remove-AppxPackage -AllUsers
    Write-Receipt 'cleaned'
} catch {
    @{ failed=$true; message=$_.Exception.ToString(); details=($_ | Out-String) } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testFolder 'install-error.json') -Encoding UTF8
    exit 1
}
