<#
    Builds the single self-contained AppSentry.exe that a release ships, and puts it in dist/
    with its SHA-256 checksum.

    The CI workflows run this same script, so the published file always matches the recipe here.

        .\publish.ps1                      # self-contained: no .NET runtime needed on the PC
        .\publish.ps1 -FrameworkDependent   # smaller, but needs the .NET 9 Desktop Runtime
        .\publish.ps1 -OutDir publish       # somewhere else
#>
param(
    [switch]$FrameworkDependent,
    [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "AppSentry/AppSentry.csproj"
$workDir = Join-Path $OutDir "publish"

& dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained:$(-not $FrameworkDependent) `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $workDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$exe = Join-Path $OutDir "AppSentry.exe"
Copy-Item (Join-Path $workDir "AppSentry.exe") $exe -Force

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  AppSentry.exe" | Set-Content -Path (Join-Path $OutDir "SHA256SUMS.txt") -Encoding ascii -NoNewline

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "AppSentry.exe  $sizeMb MB  sha256 $hash" -ForegroundColor Green
