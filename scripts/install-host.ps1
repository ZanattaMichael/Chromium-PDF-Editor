# Installs the PDF Editor native messaging host and registers it with Chromium-based
# browsers (Chrome, Chromium, Edge, Brave) on Windows.
#
# How the host binary is obtained, in priority order:
#   1. -HostDir <dir>   use an already-extracted self-contained host (e.g. the host\ folder
#                       from a release bundle) -- no download, no build.
#   2. a sibling host\  if this script sits inside an unzipped release bundle (a host\ folder
#                       next to it), that is used automatically.
#   3. -FromSource      build locally with `dotnet publish` (needs the .NET SDK).
#   4. default          download the platform bundle from the latest GitHub release and use
#                       the host\ inside it -- runtime and native libs bundled, no SDK needed.
#
# Usage:
#   .\scripts\install-host.ps1 -ExtensionId <id> [-HostDir dir] [-FromSource]
#                              [-Configuration Release] [-Repo owner/name] [-Tag vX.Y.Z]
param(
    [Parameter(Mandatory = $true)][string]$ExtensionId,
    [string]$HostDir = "",
    [switch]$FromSource,
    [string]$Configuration = "Release",
    [string]$Repo = "",
    [string]$Tag = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $env:LOCALAPPDATA "PdfEditorHost"
$hostName = "com.pdfeditor.host"
$defaultRepo = "ZanattaMichael/Chromium-PDF-Editor"

# owner/name to download from: explicit -Repo, else inferred from origin's URL, else default.
function Resolve-Repo {
    if ($Repo) { return $Repo }
    try {
        $url = git -C $repoRoot remote get-url origin 2>$null
        if ($url -match 'github\.com[:/]+([^/]+)/([^/.]+)') { return "$($Matches[1])/$($Matches[2])" }
    } catch { }
    return $defaultRepo
}

# The release tag to install: explicit -Tag, else latest final release, else newest of any kind.
function Resolve-Tag($r) {
    if ($Tag) { return $Tag }
    try { return (Invoke-RestMethod "https://api.github.com/repos/$r/releases/latest").tag_name } catch { }
    try {
        $latest = Invoke-RestMethod "https://api.github.com/repos/$r/releases?per_page=1"
        if ($latest -and $latest[0].tag_name) { return $latest[0].tag_name }
    } catch { }
    throw "Could not determine the latest release tag for $r. Pass -Tag <vX.Y.Z>."
}

# Copies an extracted self-contained host directory into $publishDir and returns the exe path.
function Install-FromDir($src) {
    $src = (Resolve-Path $src).Path
    if (-not (Test-Path (Join-Path $src "PdfEditor.NativeHost.exe"))) {
        throw "No PdfEditor.NativeHost.exe found in $src."
    }
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    Copy-Item -Path (Join-Path $src "*") -Destination $publishDir -Recurse -Force
    return (Join-Path $publishDir "PdfEditor.NativeHost.exe")
}

# When run from inside an unzipped release bundle, the host sits in a host\ folder next to it.
$bundledHost = Join-Path $repoRoot "host"

if ($HostDir) {
    Write-Host "Using the host in $HostDir..."
    $hostPath = Install-FromDir $HostDir
} elseif ($FromSource) {
    Write-Host "Building native host from source ($Configuration)..."
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish (Join-Path $repoRoot "src/PdfEditor.NativeHost") -c $Configuration -o $publishDir --nologo -v q
    # A framework-dependent build runs via the installed `dotnet`, so launch it through a .bat shim.
    $launcher = Join-Path $publishDir "pdf-editor-host.bat"
    "@echo off`r`ndotnet `"$publishDir\PdfEditor.NativeHost.dll`" %*" | Set-Content -Path $launcher -Encoding ascii
    $hostPath = $launcher
} elseif (Test-Path (Join-Path $bundledHost "PdfEditor.NativeHost.exe")) {
    Write-Host "Using the bundled host next to this script..."
    $hostPath = Install-FromDir $bundledHost
} else {
    if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") {
        throw "No prebuilt win-arm64 host is published. Use -FromSource."
    }
    $rid = "win-x64"
    $r = Resolve-Repo
    $t = Resolve-Tag $r
    $url = "https://github.com/$r/releases/download/$t/pdf-editor-bundle-$rid.zip"

    Write-Host "Downloading prebuilt bundle $t ($rid) from $r..."
    $zip = Join-Path $env:TEMP "pdf-editor-bundle-$rid.zip"
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip
    } catch {
        throw "Could not download $url. That release may not have a $rid bundle yet; try -FromSource, or -Tag/-Repo."
    }
    $extract = Join-Path $env:TEMP "pdf-editor-bundle-$rid"
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    Remove-Item $zip -Force
    if (-not (Test-Path (Join-Path $extract "host"))) {
        throw "The downloaded bundle did not contain a host\ folder."
    }
    $hostPath = Install-FromDir (Join-Path $extract "host")
}

$template = Get-Content (Join-Path $repoRoot "scripts/com.pdfeditor.host.json.template") -Raw
$manifest = $template.Replace("__HOST_PATH__", $hostPath.Replace("\", "\\")).Replace("__EXTENSION_ID__", $ExtensionId)
$manifestPath = Join-Path $publishDir "$hostName.json"
$manifest | Set-Content -Path $manifestPath -Encoding utf8

$registryRoots = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts",
    "HKCU:\Software\Chromium\NativeMessagingHosts",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts"
)
foreach ($root in $registryRoots) {
    $key = Join-Path $root $hostName
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name "(Default)" -Value $manifestPath
    Write-Host "Registered: $key"
}

Write-Host ""
Write-Host "Done. Restart your browser, then re-test from the extension's options page."
