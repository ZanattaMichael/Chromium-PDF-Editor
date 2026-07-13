# Publishes the native host and registers it with Chromium-based browsers on Windows.
# Usage: .\scripts\install-host.ps1 -ExtensionId <id> [-Configuration Release]
param(
    [Parameter(Mandatory = $true)][string]$ExtensionId,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $env:LOCALAPPDATA "PdfEditorHost"
$hostName = "com.pdfeditor.host"

Write-Host "Publishing native host ($Configuration)..."
dotnet publish (Join-Path $repoRoot "src/PdfEditor.NativeHost") -c $Configuration -o $publishDir --nologo -v q

# Chrome on Windows launches the host via a .bat shim.
$launcher = Join-Path $publishDir "pdf-editor-host.bat"
"@echo off`r`ndotnet `"$publishDir\PdfEditor.NativeHost.dll`" %*" | Set-Content -Path $launcher -Encoding ascii

$template = Get-Content (Join-Path $repoRoot "scripts/com.pdfeditor.host.json.template") -Raw
$manifest = $template.Replace("__HOST_PATH__", $launcher.Replace("\", "\\")).Replace("__EXTENSION_ID__", $ExtensionId)
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
