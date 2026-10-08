param([string]$OutputDirectory='outputs/CodexSwitcher-1.6')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.tools/cli-home'
$env:NUGET_PACKAGES=Join-Path $projectRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$sdk=Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if(-not (Test-Path -LiteralPath $sdk)) {$sdk='dotnet'}
& $sdk publish 'src/CodexSwitcher.App' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
if($LASTEXITCODE -ne 0) {throw 'Publish failed'}
Copy-Item -LiteralPath 'README.md' -Destination (Join-Path $OutputDirectory 'Инструкция.md') -Force
foreach($name in @('README.md','README.en.md','LICENSE','SECURITY.md','CONTRIBUTING.md','CHANGELOG.md'))
{Copy-Item -LiteralPath $name -Destination (Join-Path $OutputDirectory $name) -Force}
Copy-Item -LiteralPath 'docs' -Destination (Join-Path $OutputDirectory 'docs') -Recurse -Force
Get-FileHash (Join-Path $OutputDirectory 'CodexSwitcher.exe') -Algorithm SHA256



