<#
  Builds dist\AppSentry-x64.msi from dist\AppSentry.exe (run publish.ps1 first, or pass -Publish).

  Needs the WiX toolset once:   dotnet tool install --global wix --version 5.0.2
                               wix extension add --global WixToolset.UI.wixext/5.0.2

      installer\build-installer.ps1                      # MSI of what publish.ps1 just built
      installer\build-installer.ps1 -Publish             # publish first, then build the MSI
      installer\build-installer.ps1 -CertThumbprint <t>  # signed, for anything you hand out

  What the MSI does — Program Files\AppSentry for every user, a Start menu shortcut, the background service
  (unless INSTALLSERVICE=0) and the sign-in task; uninstalling keeps the history — is in installer\AppSentry.wxs.

  AppSentry Guard builds its installer from the same .wxs with its own EULA and notices — from Guard's root, so that
  the relative paths below are Guard's own files:

      external\AppSentry\installer\build-installer.ps1 -Exe artifacts\publish\AppSentry.exe `
          -LicenseFile installer\EULA.txt -Notices THIRD-PARTY-NOTICES.txt -OutDir artifacts

  Which package it builds — AppSentry, or AppSentry Guard with its own name and UpgradeCode (the .wxs says why) — is
  worked out the way Directory.Build.props decides what goes into the exe: the edition named in the first
  AppSentry.Edition.props found above this repo, otherwise the free one. So the Guard tree always gets the Guard
  package and a plain clone the free one, with nothing to pass. The last line of the output names the package.

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
    [string]$TestVersion,       # only for testing upgrades: builds the MSI with this version (x.y.z) over the same exe; never ship it
    [ValidateSet('Free', 'Guard')]
    [string]$TestEdition        # only for testing switches between editions: builds that package over this tree's exe; never ship it
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
if (-not $Exe) { $Exe = Join-Path $OutDir 'AppSentry.exe' }
if (-not $Icon) { $Icon = Join-Path $root 'AppSentry\Assets\AppSentry.ico' }
if (-not $LicenseFile) { $LicenseFile = Join-Path $PSScriptRoot 'EULA.txt' }
if (-not $Notices) { $Notices = Join-Path $root 'THIRD-PARTY-NOTICES.txt' }

# A relative parameter means relative to the current folder — Guard's root when Guard runs this script — while the
# defaults above, built from $root/$PSScriptRoot, always come from this repo (the icon, the notices, the EULA).
# Everything is made absolute before anything reads it, so publish.ps1 and wix get the same paths the caller meant.
function Resolve-FullPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return [System.IO.Path]::GetFullPath($path) }
    [System.IO.Path]::GetFullPath((Join-Path $PWD $path))
}
foreach ($name in 'OutDir', 'Exe', 'Icon', 'LicenseFile', 'Notices') {
    Set-Variable -Name $name -Value (Resolve-FullPath (Get-Variable -Name $name -ValueOnly))
}
$staging = Join-Path $OutDir 'installer-docs'

# The edition, found the way Directory.Build.props finds it (GetPathOfFileAbove from the folder above this repo: the
# nearest AppSentry.Edition.props wins). A props file that names no edition is an error rather than a free package,
# because whatever it adds still goes into the exe.
function Find-Edition {
    $dir = Split-Path $root -Parent
    while ($dir) {
        $props = Join-Path $dir 'AppSentry.Edition.props'
        if (Test-Path $props -PathType Leaf) {
            $node = ([xml](Get-Content $props -Raw)).SelectSingleNode("//*[local-name()='AppSentryEdition']")
            $name = if ($node) { $node.InnerText.Trim() } else { '' }
            if ($name -notin 'Free', 'Guard') { throw "$props names edition '$name'; the installer knows Free and Guard." }
            return $name
        }
        $dir = Split-Path $dir -Parent
    }
    'Free'
}
$Edition = Find-Edition
if ($TestEdition -and $TestEdition -ne $Edition) {
    Write-Warning "Building the $TestEdition package around this tree's $Edition exe, for testing only: never ship it."
    $Edition = $TestEdition
}
$packageName = if ($Edition -eq 'Guard') { 'AppSentry Guard' } else { 'AppSentry' }

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
    -d "Version=$version" -d "Exe=$Exe" -d "Icon=$Icon" -d "EulaRtf=$eulaRtf" -d "Docs=$staging" -d "Edition=$Edition" -o $msi
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

# ICE43 and ICE57 assume a per-user shortcut; this package is per-machine (ALLUSERS=1), so the Start menu shortcut is
# in the all-users folder and the file is the right key path.
wix msi validate -sice ICE43 -sice ICE57 $msi
if ($LASTEXITCODE -ne 0) { throw 'the MSI failed validation' }

Sign-File $msi

$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
if ($signing) { Write-Host "Built and signed $msi ($packageName $version, $size MB)." }
else { Write-Host "Built $msi ($packageName $version, $size MB), UNSIGNED: don't give it to a buyer. Re-run with -CertThumbprint <thumbprint> or -Pfx <file>." }
