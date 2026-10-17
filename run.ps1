$ErrorActionPreference = 'Stop'
$localDotnet = Join-Path $env:LOCALAPPDATA 'ZemiLog\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

& $dotnet run --project "$PSScriptRoot\src\ZemiLog.App\ZemiLog.App.csproj"
