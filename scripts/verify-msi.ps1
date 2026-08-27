# Checks that a built Windows MSI really registers the native messaging host where the browser will
# look for it. The Windows counterpart of scripts/verify-linux-package.sh.
#
# Everything about a native messaging host is silent when it is wrong: the browser reports
# "Specified native messaging host not found" whether the registry value is missing, points at a
# manifest the package never shipped, or the manifest names a different extension. This turns each
# of those into a build failure with a name.
#
# It reads the MSI's own tables through the Windows Installer API (no install required) and, when
# it can, also extracts the payload with an administrative install to check the shipped manifest.
#
# Usage: .\scripts\verify-msi.ps1 <path-to-msi> [-ExpectedExtensionId <id>]
param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [string]$ExpectedExtensionId = ""
)

$ErrorActionPreference = "Stop"

$hostName = "com.pdfeditor.host"
$manifestName = "$hostName.json"
$manifestValue = "[INSTALLFOLDER]$manifestName"

# Must match installer/windows/PdfEditorHost.wxs. On Windows the key carries no channel component
# (see the comment in that file), so this is the complete set -- one vendor path per browser family.
$expectedKeys = @(
    "SOFTWARE\Google\Chrome\NativeMessagingHosts\$hostName",
    "SOFTWARE\Chromium\NativeMessagingHosts\$hostName",
    "SOFTWARE\Microsoft\Edge\NativeMessagingHosts\$hostName",
    "SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\$hostName",
    "SOFTWARE\Vivaldi\NativeMessagingHosts\$hostName",
    "SOFTWARE\Opera Software\NativeMessagingHosts\$hostName"
)
$expectedFiles = @("PdfEditor.NativeHost.exe", $manifestName, "register-host.ps1", "extension-id.txt")

$script:failures = 0
# Named with the Show- verb rather than Add-/Write-: both write to the host for a human reading the
# build log and return nothing, so a caller must not be able to pipe or capture them as data.
function Show-Failure($message) { Write-Host "  FAIL: $message"; $script:failures++ }
function Show-Pass($message) { Write-Host "  ok:   $message" }

# --------------------------------------------------------- Windows Installer API
#
# The MSI database is queried through COM. PowerShell cannot bind to these interfaces directly
# (they are late-bound IDispatch), hence the InvokeMember calls.
function Invoke-MsiQuery($database, [string]$sql) {
    $view = $database.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $database, @($sql))
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
    $rows = @()
    while ($true) {
        $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
        if (-not $record) { break }
        $fieldCount = $record.GetType().InvokeMember("FieldCount", "GetProperty", $null, $record, $null)
        $row = @()
        for ($i = 1; $i -le $fieldCount; $i++) {
            $row += $record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, @($i))
        }
        $rows += , $row
    }
    $view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null) | Out-Null
    # The leading comma stops PowerShell unrolling the outer array on the way out.
    return , $rows
}

if (-not (Test-Path $MsiPath)) { throw "No such MSI: $MsiPath" }
$MsiPath = (Resolve-Path $MsiPath).Path
Write-Host "Verifying $MsiPath"

$installer = New-Object -ComObject WindowsInstaller.Installer
# 0 = read-only.
$database = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($MsiPath, 0))

# --------------------------------------------------------------------- version

$versionRows = Invoke-MsiQuery $database "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'ProductVersion'"
if ($versionRows.Count -ne 1) {
    Show-Failure "the MSI has no ProductVersion property"
} else {
    Show-Pass "ProductVersion is $($versionRows[0][0])"
}

# ----------------------------------------------------------------------- files

# The File table's FileName is "SHORTNAME|LongName" when a short name was generated; the long name
# after the pipe is what actually lands on disk.
$fileRows = Invoke-MsiQuery $database "SELECT ``FileName`` FROM ``File``"
$fileNames = @()
foreach ($row in $fileRows) { $fileNames += ($row[0] -split '\|')[-1] }

foreach ($expected in $expectedFiles) {
    if ($fileNames -contains $expected) {
        Show-Pass "ships $expected"
    } else {
        Show-Failure "$expected is not in the package"
    }
}

# -------------------------------------------------------------------- registry

$registryRows = Invoke-MsiQuery $database "SELECT ``Root``, ``Key``, ``Value`` FROM ``Registry``"
$byKey = @{}
foreach ($row in $registryRows) { $byKey[$row[1]] = $row }

foreach ($key in $expectedKeys) {
    if (-not $byKey.ContainsKey($key)) {
        Show-Failure "no registry value for HKLM\$key -- that browser will never find the host"
        continue
    }
    $row = $byKey[$key]
    # Root 2 is HKLM. A per-user (HKCU, root 1) value here would be written for whichever account
    # happened to run the installer, which for an elevated MSI is rarely the user's own.
    if ($row[0] -ne "2") {
        Show-Failure "$key is registered under root $($row[0]), expected 2 (HKLM)"
    } elseif ($row[2] -ne $manifestValue) {
        Show-Failure "$key points at '$($row[2])', expected '$manifestValue'"
    } else {
        Show-Pass "HKLM\$key -> $manifestValue"
    }
}

$unexpected = @($byKey.Keys | Where-Object { $expectedKeys -notcontains $_ })
if ($unexpected.Count -gt 0) {
    # Not a failure: an extra key is inert. Worth printing so a deliberate addition is visible and
    # an accidental one is noticed.
    Write-Host "  note: also registers $($unexpected -join ', ')"
}

[System.Runtime.InteropServices.Marshal]::ReleaseComObject($database) | Out-Null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null

# ------------------------------------------------------------- shipped manifest
#
# The tables above prove the registry points at a manifest the package ships; they say nothing
# about what is *in* it. An administrative install unpacks the payload without installing anything.
# If that cannot run here, say so rather than failing the build on an environment problem -- the
# checks above are the ones that catch a broken registration.
$extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pdfeditor-msi-verify-" + [guid]::NewGuid())
$logFile = Join-Path ([System.IO.Path]::GetTempPath()) ("pdfeditor-msi-verify-" + [guid]::NewGuid() + ".log")
$extracted = $false
try {
    $process = Start-Process -FilePath "msiexec.exe" `
        -ArgumentList @("/a", "`"$MsiPath`"", "/qn", "TARGETDIR=`"$extractDir`"", "/l*v", "`"$logFile`"") `
        -Wait -PassThru -NoNewWindow
    $extracted = ($process.ExitCode -eq 0)
    if (-not $extracted) { Write-Host "  note: administrative install exited $($process.ExitCode); skipping the payload checks" }
} catch {
    Write-Host "  note: could not run an administrative install ($($_.Exception.Message)); skipping the payload checks"
}

if ($extracted) {
    $manifestFile = Get-ChildItem -Path $extractDir -Filter $manifestName -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $manifestFile) {
        Show-Failure "the extracted payload has no $manifestName"
    } else {
        $manifest = Get-Content $manifestFile.FullName -Raw | ConvertFrom-Json
        if ($manifest.name -ne $hostName) {
            Show-Failure "$manifestName declares name '$($manifest.name)', expected '$hostName'"
        } else {
            Show-Pass "$manifestName declares $hostName"
        }
        if ($manifest.type -ne "stdio") { Show-Failure "$manifestName type is '$($manifest.type)', expected 'stdio'" }

        # On Windows the host path may be relative to the manifest, which is what the MSI uses so
        # the pair can live anywhere under Program Files. Either way it must resolve to a file the
        # package actually ships.
        $hostFile = Join-Path $manifestFile.DirectoryName $manifest.path
        if (Test-Path $hostFile) {
            Show-Pass "$manifestName points at $($manifest.path), which is in the package"
        } else {
            Show-Failure "$manifestName points at '$($manifest.path)', which the package does not ship"
        }

        $origins = @($manifest.allowed_origins)
        if ($origins.Count -ne 1 -or $origins[0] -notmatch '^chrome-extension://[a-p]{32}/$') {
            Show-Failure "$manifestName allowed_origins is '$($origins -join ', ')', expected a single chrome-extension://<id>/ origin"
        } else {
            Show-Pass "$manifestName allows $($origins[0])"
            if ($ExpectedExtensionId -and $origins[0] -ne "chrome-extension://$ExpectedExtensionId/") {
                Show-Failure "$manifestName pins a different extension ID than the expected $ExpectedExtensionId"
            }
        }
    }

    $registerScript = Get-ChildItem -Path $extractDir -Filter "register-host.ps1" -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($registerScript) {
        Show-Pass "ships register-host.ps1 next to the host, so an MSI install can re-register per-user"
    } else {
        Show-Failure "the extracted payload has no register-host.ps1"
    }
}

Remove-Item -Recurse -Force $extractDir -ErrorAction SilentlyContinue
Remove-Item -Force $logFile -ErrorAction SilentlyContinue

Write-Host ""
if ($script:failures -gt 0) {
    throw "$script:failures check(s) failed for $MsiPath."
}
Write-Host "All checks passed for $MsiPath."
