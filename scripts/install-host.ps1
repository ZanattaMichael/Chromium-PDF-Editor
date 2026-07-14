# Installs the PDF Editor native messaging host and registers it with Chromium-based
# browsers (Chrome, Chromium, Edge, Brave) on Windows.
#
# By default it downloads a prebuilt, self-contained host from the project's latest
# GitHub release -- the .NET runtime and native libraries are bundled, so no .NET SDK
# is required. Use -FromSource to build it locally with `dotnet publish` instead.
#
# Usage:
#   .\scripts\install-host.ps1 -ExtensionId <id> [-FromSource] [-Configuration Release]
#                              [-Repo owner/name] [-Tag vX.Y.Z]
param(
    [Parameter(Mandatory = $true)][string]$ExtensionId,
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

if ($FromSource) {
    Write-Host "Building native host from source ($Configuration)..."
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish (Join-Path $repoRoot "src/PdfEditor.NativeHost") -c $Configuration -o $publishDir --nologo -v q
    # A framework-dependent build runs via the installed `dotnet`, so launch it through a .bat shim.
    $launcher = Join-Path $publishDir "pdf-editor-host.bat"
    "@echo off`r`ndotnet `"$publishDir\PdfEditor.NativeHost.dll`" %*" | Set-Content -Path $launcher -Encoding ascii
    $hostPath = $launcher
} else {
    if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") {
        throw "No prebuilt win-arm64 host is published. Use -FromSource."
    }
    $rid = "win-x64"
    $r = Resolve-Repo
    $t = Resolve-Tag $r
    $url = "https://github.com/$r/releases/download/$t/pdf-editor-host-$rid.zip"

    Write-Host "Downloading prebuilt host $t ($rid) from $r..."
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
    $zip = Join-Path $env:TEMP "pdf-editor-host-$rid.zip"
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip
    } catch {
        throw "Could not download $url. That release may not have a $rid asset yet; try -FromSource, or -Tag/-Repo."
    }
    Expand-Archive -Path $zip -DestinationPath $publishDir -Force
    Remove-Item $zip -Force
    $hostPath = Join-Path $publishDir "PdfEditor.NativeHost.exe"
    if (-not (Test-Path $hostPath)) {
        throw "The downloaded package did not contain PdfEditor.NativeHost.exe."
    }
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
