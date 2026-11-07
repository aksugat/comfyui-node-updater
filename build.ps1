# Builds ComfyNodeUpdater.exe into dist\.
#
#   .\build.ps1                  framework-dependent, ~270 KB (needs the .NET 8 desktop runtime)
#   .\build.ps1 -SelfContained   everything bundled, ~150 MB, runs on a PC without .NET
#   .\build.ps1 -Run             build, then run the app's own 18-check self-test

param(
    [switch]$SelfContained,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root "dist"

$arguments = @(
    "publish", (Join-Path $root "src"),
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o", $out
)

Write-Host "dotnet $($arguments -join ' ')"
& dotnet @arguments

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $out "ComfyNodeUpdater.exe"
Write-Host "built $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"

if ($Run) {
    $report = Join-Path $out "self-test.txt"

    # Start-Process -Wait: PowerShell does not wait for a WinExe launched with &, so the report
    # would be read before it exists.
    $process = Start-Process -FilePath $exe -ArgumentList @("--self-test", $report) -PassThru -Wait

    Get-Content $report
    if ($process.ExitCode -ne 0) { throw "self-test failed ($($process.ExitCode))" }
}
