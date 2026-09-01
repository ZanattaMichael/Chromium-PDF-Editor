# Registers an already-installed PDF Editor native messaging host with the *current user's*
# Chromium-based browsers on Windows. The counterpart of scripts/register-host.sh on Linux/macOS.
#
# The MSI registers the host machine-wide under HKLM and pins allowed_origins to the published
# Chrome and Edge Web Store extension IDs. That is right for the common case and wrong for one: a
# developer-mode / unpacked extension has a different, machine-specific ID, so the pinned origins
# never match and the browser refuses the connection. Chromium looks in HKEY_CURRENT_USER before
# HKEY_LOCAL_MACHINE, so writing a per-user manifest here overrides the MSI's without touching it.
#
# The MSI installs this script next to the host (Program Files\PDF Editor Host\register-host.ps1)
# so someone who installed the package -- and therefore has no repository checkout -- can still run
# it. It only writes a manifest and registry values: it never downloads or builds anything, so it
# is safe to re-run.
#
# Usage:
#   .\register-host.ps1 [-ExtensionId <id>] [-EdgeExtensionId <id>] [-HostPath <path>] [-List] [-Uninstall]
param(
    [string]$ExtensionId = "",
    [string]$EdgeExtensionId = "",
    [string]$HostPath = "",
    [switch]$List,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$hostName = "com.pdfeditor.host"
# Pinned Chrome and Edge Web Store extension IDs, used when nothing else supplies one. The MSI
# drops the IDs it was built with next to the host so a rebuild for different IDs stays
# self-consistent.
$defaultExtensionId = "cbmfodojjlfppljbdebmpbcppngkkibi"
$defaultEdgeExtensionId = "kcppllhgnfmdbmglohgmabipeikopfhb"

# Per-user manifests live under LOCALAPPDATA: HKCU keys are per-user, so pointing them at a
# machine-wide file the user cannot rewrite would defeat the purpose.
$manifestDir = Join-Path $env:LOCALAPPDATA "PdfEditorHost"
$manifestPath = Join-Path $manifestDir "$hostName.json"

# Per-user (HKCU) registration keys for the common Chromium-based browsers, matching the machine-wide
# set the MSI writes (installer/windows/PdfEditorHost.wxs).
#
# Unlike Linux -- where the manifest directory is compiled in and every channel is a separate
# product -- the Windows key is a hardcoded constant with no channel component
# (chrome/browser/extensions/api/messaging/launch_context_win.cc): a Chromium-branded build tries
# SOFTWARE\Chromium\NativeMessagingHosts and then falls back to SOFTWARE\Google\Chrome\..., which
# is also what Chrome Beta, Dev and Canary read. So there are no per-channel keys to add here; the
# forks below each patch that constant to their own vendor path. Each browser reads only its own
# key, so a key no installed browser consults is simply inert.
$registryRoots = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
    "HKCU:\Software\Chromium\NativeMessagingHosts",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
    "HKCU:\Software\Vivaldi\NativeMessagingHosts",
    "HKCU:\Software\Opera Software\NativeMessagingHosts"
)

# ------------------------------------------------------------------ extension ID

if (-not $ExtensionId) { $ExtensionId = $env:CHROME_EXTENSION_ID }
if (-not $ExtensionId) {
    # Written next to this script by the MSI (and present in the release bundle's scripts\ folder).
    $idFile = Join-Path $PSScriptRoot "extension-id.txt"
    if (Test-Path $idFile) { $ExtensionId = (Get-Content $idFile -Raw).Trim() }
}
if (-not $ExtensionId) { $ExtensionId = $defaultExtensionId }

if (-not $EdgeExtensionId) { $EdgeExtensionId = $env:EDGE_EXTENSION_ID }
if (-not $EdgeExtensionId) {
    $edgeIdFile = Join-Path $PSScriptRoot "edge-extension-id.txt"
    if (Test-Path $edgeIdFile) { $EdgeExtensionId = (Get-Content $edgeIdFile -Raw).Trim() }
}
if (-not $EdgeExtensionId) { $EdgeExtensionId = $defaultEdgeExtensionId }

# A Chromium extension ID is exactly 32 characters from a-p (a base-16 digest re-encoded into
# letters), for Chrome and Edge alike. Catching a typo here beats a browser silently refusing the
# connection later.
if ($ExtensionId -cnotmatch '^[a-p]{32}$') {
    throw "'$ExtensionId' is not a valid extension ID (expected 32 characters, a-p). Find yours at chrome://extensions with Developer mode on."
}
if ($EdgeExtensionId -cnotmatch '^[a-p]{32}$') {
    throw "'$EdgeExtensionId' is not a valid Edge extension ID (expected 32 characters, a-p)."
}

# ------------------------------------------------------------------- host path

if (-not $HostPath) {
    # Both Program Files roots, because both hold real installs. `wix build` defaults to -arch x86,
    # and until scripts/package-msi.ps1 passed -arch x64 the MSI was a 32-bit package -- which
    # Windows Installer redirects to "C:\Program Files (x86)". Anyone holding one of those MSIs has
    # the host there, and they are exactly the people who need this script to find it.
    #
    # ${env:ProgramFiles(x86)} is how PowerShell names a variable whose name contains parentheses.
    # It is unset on a 32-bit Windows, and equal to $env:ProgramFiles when a 32-bit PowerShell runs
    # on a 64-bit one, so the list is filtered for empties and de-duplicated before use -- Join-Path
    # throws on a null path, and $ErrorActionPreference is Stop.
    $programFilesRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) |
        Where-Object { $_ } | Select-Object -Unique
    $candidates = @(
        # This script installed beside the host by the MSI.
        (Join-Path $PSScriptRoot "PdfEditor.NativeHost.exe"),
        # This script running from a release bundle's scripts\ folder, host\ alongside it.
        (Join-Path (Split-Path -Parent $PSScriptRoot) "host\PdfEditor.NativeHost.exe")
    ) + @($programFilesRoots | ForEach-Object { Join-Path $_ "PDF Editor Host\PdfEditor.NativeHost.exe" }) + @(
        (Join-Path $env:LOCALAPPDATA "PdfEditorHost\PdfEditor.NativeHost.exe")
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { $HostPath = $candidate; break }
    }
}
# Only writing a manifest needs a real host path; -List and -Uninstall work without one.
if (-not $HostPath -and -not $List -and -not $Uninstall) {
    $looked = ($candidates | ForEach-Object { "         $_" }) -join "`r`n"
    throw "No PDF Editor native host found. Looked in:`r`n$looked`r`n       Install the MSI first, or pass -HostPath <path>."
}
if ($HostPath) { $HostPath = [System.IO.Path]::GetFullPath($HostPath) }

# ---------------------------------------------------------------------- actions

if ($List) {
    Write-Host "Extension ID:      $ExtensionId"
    Write-Host "Edge extension ID: $EdgeExtensionId"
    if ($HostPath) { Write-Host "Host path:    $HostPath" } else { Write-Host "Host path:    <not found>" }
    Write-Host "Manifest:     $manifestPath"
    Write-Host "Registry keys:"
    foreach ($root in $registryRoots) { Write-Host ("  " + (Join-Path $root $hostName)) }
    exit 0
}

if ($Uninstall) {
    $removed = 0
    foreach ($root in $registryRoots) {
        $key = Join-Path $root $hostName
        if (Test-Path $key) {
            Remove-Item -Path $key -Recurse -Force
            Write-Host "Removed: $key"
            $removed++
        }
    }
    if (Test-Path $manifestPath) {
        Remove-Item -Path $manifestPath -Force
        Write-Host "Removed: $manifestPath"
    }
    Write-Host ""
    Write-Host "Removed $removed per-user registration(s). The machine-wide HKLM keys belong to the"
    Write-Host "MSI -- remove those from Settings > Apps, or with msiexec /x."
    exit 0
}

New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null

# The manifest's "path" is absolute here (unlike the MSI's, which is relative to itself) because
# this manifest lives under LOCALAPPDATA while the host it points at usually does not. JSON needs
# the backslashes doubled.
$escapedHostPath = $HostPath.Replace("\", "\\")
$manifest = @"
{
  "name": "$hostName",
  "description": "PDF Editor native messaging host (C#/.NET)",
  "path": "$escapedHostPath",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://$ExtensionId/",
    "chrome-extension://$EdgeExtensionId/"
  ]
}
"@
Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8

foreach ($root in $registryRoots) {
    $key = Join-Path $root $hostName
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(Default)" -Value $manifestPath
    Write-Host "Registered: $key"
}

Write-Host ""
Write-Host "Registered the host for this user in $($registryRoots.Count) location(s)."
Write-Host "  host path:         $HostPath"
Write-Host "  extension ID:      $ExtensionId"
Write-Host "  Edge extension ID: $EdgeExtensionId"
Write-Host "  manifest:          $manifestPath"

# The host is only useful if it actually starts. A host that exits immediately is reported by the
# browser as one that "has exited", which looks much like a host that was never installed -- say so
# here instead. Never fatal: the registration itself succeeded either way.
$started = $false
try {
    & $HostPath --version 2>&1 | Out-Null
    $started = ($LASTEXITCODE -eq 0)
} catch {
    $started = $false
}
if (-not $started) {
    Write-Host ""
    Write-Warning "$HostPath did not start. Run it directly to see why:"
    Write-Host "    & `"$HostPath`" --diagnostics"
}

Write-Host ""
Write-Host "Restart your browser, then re-test from the PDF Editor options page."
