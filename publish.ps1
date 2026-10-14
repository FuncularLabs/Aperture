# Publishes Aperture as a framework-dependent Windows app into .\publish\.
# Requires the .NET 10 Desktop Runtime on the target machine.
#
# The output is ONE file: Aperture.exe carries its native DLLs (e_sqlite3, libSkiaSharp,
# Microsoft.Data.SqlClient.SNI) and extracts them at startup. The script fails if a
# native DLL is left loose beside the exe, because an exe copied on its own then dies at
# startup with DllNotFoundException (0.8.6-beta1).
#
# Usage:  pwsh ./publish.ps1            (framework-dependent, small)
#         pwsh ./publish.ps1 -SelfContained   (bundles the runtime, large but portable)

param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "publish"

# Build output only (gitignored). A stale DLL from an earlier publish would both
# mislead the check below and get swept into an install.
if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}

$publishArgs = @(
    "publish", (Join-Path $PSScriptRoot "src/Aperture.App/Aperture.App.csproj"),
    "-c", "Release",
    "-r", "win-x64",
    "-o", $out,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true"
)

if ($SelfContained) {
    $publishArgs += @("--self-contained", "true")
} else {
    $publishArgs += @("--self-contained", "false")
}

Write-Host "Publishing Aperture ($([bool]$SelfContained ? 'self-contained' : 'framework-dependent'))..." -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)."
}

$exe = Join-Path $out "Aperture.exe"
if (-not (Test-Path $exe)) {
    throw "Publish produced no $exe."
}

$loose = Get-ChildItem $out -Filter *.dll -File
if ($loose) {
    throw "Native libraries were not bundled into Aperture.exe: $($loose.Name -join ', '). " +
          "An exe copied without them does not start. Check IncludeNativeLibrariesForSelfExtract."
}

Write-Host "`nDone -> $exe (natives bundled; no loose DLLs)" -ForegroundColor Green
