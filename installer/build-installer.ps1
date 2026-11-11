<#
  Builds dist\AppSentry-x64.msi from dist\AppSentry.exe (run publish.ps1 first, or pass -Publish).

  Needs the WiX toolset once:   dotnet tool install --global wix --version 5.0.2
                               wix extension add --global WixToolset.UI.wixext/5.0.2

      installer\build-installer.ps1                      # MSI of what publish.ps1 just built
      installer\build-installer.ps1 -Publish             # publish first, then build the MSI
      installer\build-installer.ps1 -CertThumbprint <t>  # signed, for anything you hand out

  What the MSI does — Program Files\AppSentry for every user, a Start menu shortcut, the background service
  (unless INSTALLSERVICE=0) and the sign-in task; uninstalling keeps the history — is in installer\AppSentry.wxs.

  AppSentry Guard builds its installer from the same .wxs with its own EULA and notices:

      external\AppSentry\installer\build-installer.ps1 -Exe artifacts\publish\AppSentry.exe `
          -LicenseFile installer\EULA.txt -Notices THIRD-PARTY-NOTICES.txt -OutDir artifacts

  Signing (do this for anything you give to a buyer): pass the thumbprint of a code-signing certificate that is in
  your certificate store, or a .pfx file (its password in the APPSENTRY_PFX_PASSWORD environment variable):
      installer\build-installer.ps1 -Publish -CertThumbprint <thumbprint>
      installer\build-installer.ps1 -Publish -Pfx C:\keys\codesign.pfx
  The exe is signed first, so the signed one goes into the MSI, then the MSI is signed. Both are timestamped, so the
  signatures stay valid after the certificate expires. Without either parameter the build is unsigned and says so.
#>
param(
    [switch]$Publish,
    [string]$Exe,               # defaults to dist\AppSentry.exe
    [string]$Icon,              # defaults to AppSentry\Assets\AppSentry.ico
    [string]$LicenseFile,       # defaults to installer\EULA.txt (MIT); Guard passes its own EULA
    [string]$Notices,           # defaults to THIRD-PARTY-NOTICES.txt
    [string]$OutDir,            # defaults to dist
    [string]$CertThumbprint,
    [string]$Pfx,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$NoTimestamp,       # only for trying the signing step offline with a throwaway certificate
    [string]$TestVersion        # only for testing upgrades: builds the MSI with this version (x.y.z) over the same exe; never ship it
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
if (-not $Exe) { $Exe = Join-Path $OutDir 'AppSentry.exe' }
if (-not $Icon) { $Icon = Join-Path $root 'AppSentry\Assets\AppSentry.ico' }
if (-not $LicenseFile) { $LicenseFile = Join-Path $PSScriptRoot 'EULA.txt' }
if (-not $Notices) { $Notices = Join-Path $root 'THIRD-PARTY-NOTICES.txt' }
$staging = Join-Path $OutDir 'installer-docs'

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw 'wix not found: dotnet tool install --global wix --version 5.0.2 && wix extension add --global WixToolset.UI.wixext/5.0.2'
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$signing = [bool]($CertThumbprint -or $Pfx)
if ($signing -and -not $signtool) { throw 'signtool.exe not found: install the Windows SDK (Signing Tools for Desktop Apps).' }

function Sign-File([string]$file) {
    if (-not $signing) { return }
    $args = @('sign', '/fd', 'SHA256')
    if ($CertThumbprint) { $args += @('/sha1', $CertThumbprint) }
    else {
        if (-not $env:APPSENTRY_PFX_PASSWORD) { throw 'Set APPSENTRY_PFX_PASSWORD to the .pfx password.' }
        $args += @('/f', $Pfx, '/p', $env:APPSENTRY_PFX_PASSWORD)
    }
    if (-not $NoTimestamp) { $args += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    & $signtool @args $file
    if ($LASTEXITCODE -ne 0) { throw "signing $file failed" }
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'   # signtool writes its complaints to stderr
    & $signtool verify /pa $file 2>&1 | Out-Null
    $verified = $LASTEXITCODE
    $ErrorActionPreference = $previous
    # A throwaway certificate isn't trusted, so verification only matters for a real one.
    if ($verified -ne 0 -and -not $NoTimestamp) { throw "$file is signed but doesn't verify" }
}

if ($Publish -or -not (Test-Path $Exe)) {
    & (Join-Path $root 'publish.ps1') -OutDir $OutDir
    if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
}

$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
if ($props -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw 'No <Version> in Directory.Build.props' }
$version = $Matches[1]
$projectVersion = $version
if ($TestVersion) { $version = $TestVersion }

# The exe's own version must match what the installer says, or Apps and features and the app disagree.
$exeVersion = (Get-Item $Exe).VersionInfo.ProductVersion -replace '[+-].*$', ''
if ($exeVersion -notlike "$projectVersion*") { throw "AppSentry.exe is version $exeVersion but the project says ${projectVersion}; publish again (-Publish)." }

Sign-File $Exe   # before the MSI is built, so the signed exe is the one inside it

New-Item -ItemType Directory -Force $staging | Out-Null
# Kept under these names in the package, so an upgrade replaces them instead of leaving the old ones behind.
Copy-Item $LicenseFile (Join-Path $staging 'EULA.txt') -Force
Copy-Item $Notices (Join-Path $staging 'THIRD-PARTY-NOTICES.txt') -Force

# The license text as RTF for the license page: escape the RTF specials, keep one paragraph per line, and write
# anything outside ASCII as a \u escape.
$text = Get-Content $LicenseFile -Raw -Encoding UTF8
$sb = [System.Text.StringBuilder]::new()
foreach ($ch in $text.ToCharArray()) {
    switch ($ch) {
        '\' { [void]$sb.Append('\\') }
        '{' { [void]$sb.Append('\{') }
        '}' { [void]$sb.Append('\}') }
        "`r" { }
        "`n" { [void]$sb.Append("\par`n") }
        default { if ([int]$ch -gt 126) { [void]$sb.Append("\u$([int]$ch)?") } else { [void]$sb.Append($ch) } }
    }
}
$rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Segoe UI;}}\f0\fs20 $($sb.ToString())}"
$eulaRtf = Join-Path $staging 'EULA.rtf'
[System.IO.File]::WriteAllText($eulaRtf, $rtf, [System.Text.Encoding]::ASCII)

$msi = Join-Path $OutDir 'AppSentry-x64.msi'
wix build (Join-Path $PSScriptRoot 'AppSentry.wxs') -arch x64 -ext WixToolset.UI.wixext `
    -d "Version=$version" -d "Exe=$Exe" -d "Icon=$Icon" -d "EulaRtf=$eulaRtf" -d "Docs=$staging" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

# ICE43 and ICE57 assume a per-user shortcut; this package is per-machine (ALLUSERS=1), so the Start menu shortcut is
# in the all-users folder and the file is the right key path.
wix msi validate -sice ICE43 -sice ICE57 $msi
if ($LASTEXITCODE -ne 0) { throw 'the MSI failed validation' }

Sign-File $msi

$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
if ($signing) { Write-Host "Built and signed $msi ($size MB, version $version)." }
else { Write-Host "Built $msi ($size MB, version $version), UNSIGNED: don't give it to a buyer. Re-run with -CertThumbprint <thumbprint> or -Pfx <file>." }
