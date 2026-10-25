#Requires -Version 7.0
param(
    [ValidateSet('x64','x86')][string]$Architecture = 'x64',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $root '.nuget\http-cache'
$env:NUGET_SCRATCH = Join-Path $root '.nuget\scratch'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$projectPath = Join-Path $root 'src\TxtReader.WindowsLegacy\TxtReader.WindowsLegacy.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = $project.Project.PropertyGroup.InformationalVersion
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "dist\Qidu-$version-compat-$Architecture" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) {
    throw 'Use a new, empty output directory; existing packages must be preserved.'
}
dotnet publish $projectPath -c Release -r "win-$Architecture" --self-contained false -o $OutputDirectory --nologo `
    "-p:PlatformTarget=$Architecture" -p:DebugType=None -p:RestoreLockedMode=true "-p:RestoreConfigFile=$(Join-Path $root 'NuGet.Config')"
if ($LASTEXITCODE -ne 0) { throw "Compatibility build failed: $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'third-party') -Destination $OutputDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $root 'docs\compatibility-preview.txt') -Destination (Join-Path $OutputDirectory 'Quickstart.txt')
Write-Output "Compatibility preview: $OutputDirectory"
