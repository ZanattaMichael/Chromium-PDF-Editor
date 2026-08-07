# Builds the Windows MSI for the PDF Editor native messaging host.
#
# Requires the WiX toolset v4+ (`dotnet tool install --global wix`) and the .NET SDK, and runs on
# Windows (an MSI can only be built on Windows). Intended for a `windows-latest` CI runner; it is
# not built in the Linux dev box.
#
# It publishes the self-contained win-x64 host, renders the native-messaging manifest with the
# pinned extension ID (the manifest's "path" is relative to itself, which Chrome allows on Windows),
# and invokes `wix build` against installer/windows/PdfEditorHost.wxs.
#
# Usage: .\scripts\package-msi.ps1 [-OutputDir dist]
param(
    [string]$OutputDir = "dist"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$version = (Get-Content (Join-Path $repoRoot "extension\manifest.json") -Raw | ConvertFrom-Json).version

# Pinned extension ID: $env:CHROME_EXTENSION_ID wins, else the committed extension-id.txt.
$extensionId = $env:CHROME_EXTENSION_ID
if (-not $extensionId) {
    $extensionId = (Get-Content (Join-Path $PSScriptRoot "extension-id.txt") -Raw).Trim()
}
if (-not $extensionId) { throw "No extension ID (set CHROME_EXTENSION_ID or provide extension-id.txt)." }

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("pdfeditor-msi-" + [guid]::NewGuid())
$hostDir = Join-Path $stage "host"
New-Item -ItemType Directory -Force -Path $hostDir | Out-Null

Write-Host "Publishing native host (win-x64, self-contained)..."
dotnet publish (Join-Path $repoRoot "src\PdfEditor.NativeHost") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false --output $hostDir --nologo -v q

# Render the native-messaging manifest with a relative exe path and the pinned allowed origin.
$template = Get-Content (Join-Path $PSScriptRoot "com.pdfeditor.host.json.template") -Raw
$manifestJson = Join-Path $stage "com.pdfeditor.host.json"
$template.Replace("__HOST_PATH__", "PdfEditor.NativeHost.exe").Replace("__EXTENSION_ID__", $extensionId) |
    Set-Content -Path $manifestJson -Encoding UTF8

$outDirFull = Join-Path $repoRoot $OutputDir
New-Item -ItemType Directory -Force -Path $outDirFull | Out-Null
$msiPath = Join-Path $outDirFull "pdf-editor-host-$version-x64.msi"

Write-Host "Building $msiPath ..."
wix build (Join-Path $repoRoot "installer\windows\PdfEditorHost.wxs") `
    -d "Version=$version" -d "HostDir=$hostDir" -d "ManifestJson=$manifestJson" `
    -o $msiPath

Write-Host "Built: $msiPath"
Write-Host "Extension ID pinned: $extensionId"
