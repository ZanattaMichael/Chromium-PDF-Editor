<#
.SYNOPSIS
    Bootstraps a local development environment for PDF Editor.
.DESCRIPTION
    Restores .NET dependencies and pinned tools, generates the extension icons, and
    (optionally) installs the Playwright end-to-end test dependencies.

    Equivalent to scripts/bootstrap.py for contributors who prefer Python -- run
    whichever matches your platform/taste, they do the same thing. Works in both
    Windows PowerShell 5.1 and PowerShell 7+ (pwsh), on Windows, Linux, or macOS.
.PARAMETER SkipE2E
    Skip installing e2e (Playwright) dependencies.
.EXAMPLE
    ./scripts/bootstrap.ps1
.EXAMPLE
    ./scripts/bootstrap.ps1 -SkipE2E
#>
[CmdletBinding()]
param(
    [switch]$SkipE2E
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "== $Title ==" -ForegroundColor Cyan
}

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string[]]$Command,
        [string]$WorkingDirectory = $RepoRoot,
        [string]$RequiredTool
    )
    if ($RequiredTool -and -not (Get-Command $RequiredTool -ErrorAction SilentlyContinue)) {
        Write-Host "  skipped ($RequiredTool not found on PATH)"
        return $false
    }
    Write-Host "> $($Command -join ' ')   (in $WorkingDirectory)"
    Push-Location $WorkingDirectory
    try {
        & $Command[0] $Command[1..($Command.Length - 1)]
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $($Command -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
    return $true
}

Write-Host "PDF Editor -- development environment bootstrap"

Write-Section "Checking prerequisites"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "'dotnet' was not found on PATH. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}
# Windows Python installs commonly expose only "python", not "python3"; check both.
$PythonCmd = @("python3", "python") | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $PythonCmd) {
    Write-Error "Python 3 was not found on PATH (tried 'python3' and 'python'). Install it from https://www.python.org/downloads/"
    exit 1
}
$dotnetVersion = (& dotnet --version)
Write-Host ".NET SDK $dotnetVersion found; using Python via '$PythonCmd'."

Write-Section "Restoring .NET packages"
Invoke-Step -Command @("dotnet", "restore") | Out-Null

Write-Section "Restoring pinned .NET tools (reportgenerator)"
Invoke-Step -Command @("dotnet", "tool", "restore") | Out-Null

Write-Section "Generating extension icons"
Invoke-Step -Command @($PythonCmd, "scripts/generate-icons.py") | Out-Null

if ($SkipE2E) {
    Write-Section "Skipping e2e (Playwright) setup (-SkipE2E)"
}
else {
    Write-Section "Installing e2e test dependencies"
    $e2eDir = Join-Path $RepoRoot "e2e"
    $npmOk = Invoke-Step -Command @("npm", "install") -WorkingDirectory $e2eDir -RequiredTool "npm"
    if ($npmOk) {
        Write-Section "Installing Playwright's Chromium build"
        Invoke-Step -Command @("npx", "playwright", "install", "--with-deps", "chromium") -WorkingDirectory $e2eDir | Out-Null
    }
    else {
        Write-Host "Node.js/npm not found -- skipping e2e setup. Install Node 22+ to run the Playwright suite (see the e2e/ section of the README)."
    }
}

Write-Section "Done"
Write-Host @"

Next steps:
  dotnet test                     # run the .NET unit + integration suite
  ./scripts/coverage.sh           # run tests with coverage, print a summary
  cd e2e; npx playwright test     # run the browser end-to-end suite
  ./scripts/package-extension.sh  # build a Chrome Web Store-ready zip

See CONTRIBUTING.md for the full development workflow.
"@
