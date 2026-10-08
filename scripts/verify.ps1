param([switch]$ProtocolSmoke,[switch]$Preview)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.tools/cli-home'
$env:NUGET_PACKAGES=Join-Path $projectRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$sdk=Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if(-not (Test-Path -LiteralPath $sdk)) {$sdk='dotnet'}
New-Item -ItemType Directory -Path 'work' -Force | Out-Null
& $sdk run --project 'tests/CodexSwitcher.Tests' -c Release
if($LASTEXITCODE -ne 0) {throw 'Synthetic tests failed'}
& $sdk build 'src/CodexSwitcher.App' -c Release
if($LASTEXITCODE -ne 0) {throw 'Application build failed'}
if($ProtocolSmoke) {
  & 'tests/CodexSwitcher.Tests/bin/Release/net10.0-windows/CodexSwitcher.Tests.exe' --probe (Join-Path $projectRoot 'work/protocol-smoke.json')
  if($LASTEXITCODE -ne 0) {throw 'Isolated protocol smoke failed'}
}
if($Preview) {
  $exe=Join-Path $projectRoot 'src/CodexSwitcher.App/bin/Release/net10.0-windows/CodexSwitcher.exe'
  foreach($state in @('empty','saved','recovery')) {
    $output=Join-Path $projectRoot ('work/preview-'+$state+'.png')
    $argsText='--preview "'+$output+'" '+$state
    $process=Start-Process -FilePath $exe -ArgumentList $argsText -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(15000)) {throw 'Preview timeout'}
    if($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $output)) {throw 'Preview render failed'}
  }
  foreach($state in @('empty','saved')) {
    $output=Join-Path $projectRoot ('work/preview-tray-'+$state+'.png')
    $process=Start-Process -FilePath $exe -ArgumentList ('--preview-tray "'+$output+'" '+$state) -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(15000)) {throw 'Tray preview timeout'}
    if($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $output)) {throw 'Tray preview failed'}
  }
  foreach($state in @('empty','saved','recovery')) {
    $output=Join-Path $projectRoot ('work/preview-dark-'+$state+'.png')
    $process=Start-Process -FilePath $exe -ArgumentList ('--preview "'+$output+'" '+$state+' --dark') -WindowStyle Hidden -PassThru
    if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $output)) {throw 'Dark preview failed'}
  }
  $output=Join-Path $projectRoot 'work/preview-tray-dark.png'
  $process=Start-Process -FilePath $exe -ArgumentList ('--preview-tray "'+$output+'" saved --dark') -WindowStyle Hidden -PassThru
  if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $output)) {throw 'Dark tray preview failed'}
  $report=Join-Path $projectRoot 'work/theme-check.json'
  $process=Start-Process -FilePath $exe -ArgumentList ('--theme-check "'+$report+'"') -WindowStyle Hidden -PassThru
  if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) {throw 'Theme persistence/live switch check failed'}
}
Write-Output 'Verification passed. No real credentials were used or switched.'
