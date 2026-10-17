$ErrorActionPreference = 'Stop'
$localDotnet = Join-Path $env:LOCALAPPDATA 'ZemiLog\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

& $dotnet restore "$PSScriptRoot\ZemiLog.slnx"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build "$PSScriptRoot\ZemiLog.slnx" --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet test "$PSScriptRoot\ZemiLog.slnx" --no-build
exit $LASTEXITCODE
