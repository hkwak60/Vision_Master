$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet"
$env:NUGET_PACKAGES = Join-Path $root ".packages"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

& $dotnet restore (Join-Path $root "KickoutMonitor.sln") --configfile (Join-Path $root "NuGet.Config")
& $dotnet test (Join-Path $root "tests\KickoutMonitor.Tests\KickoutMonitor.Tests.csproj") -c Release --no-restore
& $dotnet publish (Join-Path $root "src\KickoutMonitor.App\KickoutMonitor.App.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o (Join-Path $root "publish")

Write-Host "Release ready: $(Join-Path $root 'publish\KickoutMonitor.exe')"
