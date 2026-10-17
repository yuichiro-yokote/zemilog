$ErrorActionPreference = 'Stop'
$localDotnet = Join-Path $env:LOCALAPPDATA 'ZemiLog\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$output = Join-Path $PSScriptRoot 'artifacts\publish\win-x64'

& $dotnet publish "$PSScriptRoot\src\ZemiLog.App\ZemiLog.App.csproj" `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "発行先: $output"
