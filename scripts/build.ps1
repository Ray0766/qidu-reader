#Requires -Version 7.0
param([switch]$Publish, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot '.nuget\http-cache'
$env:NUGET_SCRATCH = Join-Path $projectRoot '.nuget\scratch'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$appProject = Join-Path $projectRoot 'src\TxtReader.App\TxtReader.App.csproj'
$restoreConfig = Join-Path $projectRoot 'NuGet.Config'
if ($Publish) {
    if (!$OutputDirectory) {
        [xml]$project = Get-Content -LiteralPath $appProject -Raw
        $OutputDirectory = Join-Path $projectRoot "dist\TxtReader-$($project.Project.PropertyGroup.Version)-win-x64"
    }
    if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) {
        throw 'Preserve the existing publish folder; use a new version or an empty OutputDirectory.'
    }
    dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $OutputDirectory --nologo "-p:RestoreConfigFile=$restoreConfig" -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'third-party') -Destination $OutputDirectory -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.txt')
} else {
    dotnet build $appProject -c Release --nologo "-p:RestoreConfigFile=$restoreConfig" -p:RestoreLockedMode=true
}
exit $LASTEXITCODE
