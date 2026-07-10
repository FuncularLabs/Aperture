# Publishes Aperture as a framework-dependent Windows app into .\publish\.
# Requires the .NET 10 Desktop Runtime on the target machine.
#
# Usage:  pwsh ./publish.ps1            (framework-dependent, small)
#         pwsh ./publish.ps1 -SelfContained   (bundles the runtime, large but portable)

param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "publish"

$args = @(
    "publish", (Join-Path $PSScriptRoot "src/Aperture.App/Aperture.App.csproj"),
    "-c", "Release",
    "-r", "win-x64",
    "-o", $out,
    "-p:PublishSingleFile=true"
)

if ($SelfContained) {
    $args += @("--self-contained", "true")
} else {
    $args += @("--self-contained", "false")
}

Write-Host "Publishing Aperture ($([bool]$SelfContained ? 'self-contained' : 'framework-dependent'))..." -ForegroundColor Cyan
& dotnet @args

Write-Host "`nDone -> $out\Aperture.exe" -ForegroundColor Green
