$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet"
$env:NUGET_PACKAGES = Join-Path $root ".packages"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

& $dotnet restore (Join-Path $root "KickoutMonitor.sln") --configfile (Join-Path $root "NuGet.Config")
if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
& $dotnet test (Join-Path $root "tests\KickoutMonitor.Tests\KickoutMonitor.Tests.csproj") -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
& $dotnet publish (Join-Path $root "src\KickoutMonitor.App\KickoutMonitor.App.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o (Join-Path $root "publish")
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$release = Join-Path $root "publish\VisionMaster.exe"
Write-Host "Release ready: $release"

$git = Join-Path $env:ProgramFiles "Git\cmd\git.exe"
$repoRoot = (& $git -C $root rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repoRoot)) {
    throw "Could not locate the Git repository. The release was built but not posted."
}

& $git -C $repoRoot add -A -- "KickoutMonitor"
if ($LASTEXITCODE -ne 0) { throw "Could not stage the Vision Master release." }

& $git -C $repoRoot diff --cached --quiet
if ($LASTEXITCODE -eq 1) {
    $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    & $git -C $repoRoot commit -m "Publish VisionMaster $stamp"
    if ($LASTEXITCODE -ne 0) { throw "Release was built but the Git commit failed." }
}
elseif ($LASTEXITCODE -ne 0) {
    throw "Could not inspect staged release changes."
}

$branch = (& $git -C $repoRoot branch --show-current).Trim()
if ([string]::IsNullOrWhiteSpace($branch)) { throw "Cannot push from a detached HEAD." }
& $git -C $repoRoot push origin $branch
if ($LASTEXITCODE -ne 0) { throw "Release was committed locally but the GitHub push failed." }

Write-Host "Posted release to origin/$branch"
