#Requires -Version 7.0
<#
.SYNOPSIS
    Convert SonarQube Cloud issues into a SARIF 2.1.0 report for GitHub code scanning.

.DESCRIPTION
    SonarQube Cloud has no SARIF export of its own (sonar.sarifReportPaths is the opposite
    direction -- importing external SARIF *into* Sonar), so findings are read back from its web
    API and translated here. The result is uploaded with github/codeql-action/upload-sarif, which
    puts each finding in the repository's Security tab and annotates the lines it touches on a
    pull request -- so reviewers no longer have to leave GitHub to see what Sonar flagged.

    Written as a script in this repository rather than pulled in as a marketplace action on
    purpose: every action in these workflows is pinned by commit SHA, and issue #56 was largely
    about removing unpinned npx/curl supply-chain exposure. A third-party converter would
    reintroduce exactly that.

.PARAMETER SelfTest
    Run the built-in assertions instead of converting. The workflow runs this before the real
    conversion so the translation is covered by CI.

.EXAMPLE
    ./sonar-to-sarif.ps1 -Issues issues.json -ProjectKey KEY -Organization org -Output sonar.sarif

.EXAMPLE
    ./sonar-to-sarif.ps1 -SelfTest
#>
[CmdletBinding(DefaultParameterSetName = 'Convert')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Convert')]
    [string] $Issues,

    [Parameter(Mandatory, ParameterSetName = 'Convert')]
    [string] $ProjectKey,

    [Parameter(ParameterSetName = 'Convert')]
    [string] $Organization,

    [Parameter(ParameterSetName = 'Convert')]
    [string] $Output = 'sonar.sarif',

    [Parameter(Mandatory, ParameterSetName = 'SelfTest')]
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SarifSchema = 'https://json.schemastore.org/sarif-2.1.0.json'

# Sonar severities -> SARIF levels. SARIF only has error/warning/note/none, so the two "must fix"
# Sonar bands collapse onto error and the two advisory ones onto note.
$script:LevelBySeverity = @{
    BLOCKER = 'error'; CRITICAL = 'error'; MAJOR = 'warning'; MINOR = 'note'; INFO = 'note'
}

# Newer Sonar payloads carry impacts[].severity instead of the legacy top-level severity.
$script:LevelByImpact = @{
    BLOCKER = 'error'; HIGH = 'error'; MEDIUM = 'warning'; LOW = 'note'; INFO = 'note'
}

<#
Reads a property off a PSCustomObject that may simply not have it. Sonar omits keys rather than
nulling them, and Set-StrictMode turns a missing-property access into a terminating error.
#>
function Get-Field {
    param($Object, [string] $Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] } else { return $Default }
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

<# The SARIF level for an issue, preferring the newer impacts shape when present. #>
function Get-SarifLevel {
    param($Issue)
    foreach ($impact in @(Get-Field $Issue 'impacts' @())) {
        $severity = [string](Get-Field $impact 'severity' '')
        if ($severity -and $script:LevelByImpact.ContainsKey($severity.ToUpperInvariant())) {
            return $script:LevelByImpact[$severity.ToUpperInvariant()]
        }
    }
    $legacy = [string](Get-Field $Issue 'severity' '')
    if ($legacy -and $script:LevelBySeverity.ContainsKey($legacy.ToUpperInvariant())) {
        return $script:LevelBySeverity[$legacy.ToUpperInvariant()]
    }
    return 'warning'
}

<#
Strips Sonar's "projectKey:" (or "projectKey:branch:") prefix off a component key. Sonar
identifies a file as "ZanattaMichael_Chromium-PDF-Editor:src/Foo.cs"; SARIF wants the
repository-relative "src/Foo.cs" so GitHub can map the finding onto the diff.
#>
function Get-RelativePath {
    param([string] $Component, [string] $ProjectKey)
    $prefix = "${ProjectKey}:"
    if ($Component.StartsWith($prefix)) { return $Component.Substring($prefix.Length) }
    # Fall back to the last colon-separated segment, which is the path for every shape Sonar
    # currently emits. Paths themselves never contain a colon in this repository.
    if ($Component.Contains(':')) { return $Component.Substring($Component.LastIndexOf(':') + 1) }
    return $Component
}

<#
SARIF region for an issue, or $null when Sonar reported no position. SARIF columns are 1-based
while Sonar's textRange offsets are 0-based, so every offset is shifted by one. An endColumn that
would land before startColumn is dropped rather than emitted invalid.
#>
function Get-SarifRegion {
    param($Issue)
    $textRange = Get-Field $Issue 'textRange'
    $startLine = Get-Field $textRange 'startLine'
    if ($null -eq $startLine) { $startLine = Get-Field $Issue 'line' }
    if ($null -eq $startLine) { return $null }

    $region = [ordered]@{ startLine = [int] $startLine }

    $endLine = Get-Field $textRange 'endLine'
    if ($null -ne $endLine) {
        $region['endLine'] = [Math]::Max([int] $endLine, $region['startLine'])
    }

    $startOffset = Get-Field $textRange 'startOffset'
    if ($null -ne $startOffset) { $region['startColumn'] = [int] $startOffset + 1 }

    $endOffset = Get-Field $textRange 'endOffset'
    if ($null -ne $endOffset) {
        $endColumn = [int] $endOffset + 1
        $effectiveEndLine = if ($region.Contains('endLine')) { $region['endLine'] } else { $region['startLine'] }
        $effectiveStartColumn = if ($region.Contains('startColumn')) { $region['startColumn'] } else { 1 }
        # Only meaningful when the range is on one line, or the end is on a later line.
        if ($effectiveEndLine -gt $region['startLine'] -or $endColumn -ge $effectiveStartColumn) {
            $region['endColumn'] = $endColumn
        }
    }
    return $region
}

<# Deep link to the rule's description. #>
function Get-RuleHelpUri {
    param([string] $RuleKey, [string] $Organization)
    if ($Organization) {
        return "https://sonarcloud.io/organizations/$Organization/rules?open=$RuleKey&rule_key=$RuleKey"
    }
    return "https://rules.sonarsource.com/?search=$RuleKey"
}

<# Builds a SARIF 2.1.0 document (as a hashtable graph) from Sonar issues. #>
function Convert-ToSarif {
    param($Issues, [string] $ProjectKey, [string] $Organization)

    $ruleIndex = @{}
    $rules = [System.Collections.Generic.List[object]]::new()
    $results = [System.Collections.Generic.List[object]]::new()

    foreach ($issue in @($Issues)) {
        $ruleKey = [string](Get-Field $issue 'rule' '')
        $component = [string](Get-Field $issue 'component' '')
        # A project-level finding has nothing to anchor to in the diff.
        if (-not $ruleKey -or -not $component) { continue }

        $path = Get-RelativePath -Component $component -ProjectKey $ProjectKey
        if (-not $path -or $path.EndsWith('/')) { continue }  # a directory/module, not a file

        $message = [string](Get-Field $issue 'message' $ruleKey)

        if (-not $ruleIndex.ContainsKey($ruleKey)) {
            $ruleIndex[$ruleKey] = $rules.Count
            $tags = @()
            $type = Get-Field $issue 'type'
            if ($type) { $tags = @([string] $type) }
            $rules.Add([ordered]@{
                id               = $ruleKey
                name             = $ruleKey
                shortDescription = [ordered]@{ text = $message }
                helpUri          = Get-RuleHelpUri -RuleKey $ruleKey -Organization $Organization
                properties       = [ordered]@{ tags = [object[]] $tags }
            })
        }

        $physical = [ordered]@{ artifactLocation = [ordered]@{ uri = $path } }
        $region = Get-SarifRegion -Issue $issue
        if ($null -ne $region) { $physical['region'] = $region }

        $result = [ordered]@{
            ruleId    = $ruleKey
            ruleIndex = $ruleIndex[$ruleKey]
            level     = Get-SarifLevel -Issue $issue
            message   = [ordered]@{ text = $message }
            locations = [object[]] @([ordered]@{ physicalLocation = $physical })
        }
        # Stable identity so GitHub can track a finding across runs rather than re-raising it.
        $hash = Get-Field $issue 'hash'
        if ($hash) { $result['partialFingerprints'] = [ordered]@{ sonarIssueHash = [string] $hash } }
        $results.Add($result)
    }

    return [ordered]@{
        '$schema' = $script:SarifSchema
        version   = '2.1.0'
        runs      = [object[]] @(
            [ordered]@{
                tool = [ordered]@{
                    driver = [ordered]@{
                        name           = 'SonarQube Cloud'
                        informationUri = 'https://sonarcloud.io'
                        rules          = [object[]] $rules.ToArray()
                    }
                }
                results = [object[]] $results.ToArray()
            }
        )
    }
}

<# Accepts either a raw api/issues/search response or a bare list of issues. #>
function Read-Issues {
    param([string] $Raw)
    $data = $Raw | ConvertFrom-Json
    if ($null -eq $data) { return , @() }

    $issues = $null
    if ($data -isnot [System.Array]) {
        $issues = Get-Field $data 'issues'
        # A search response that simply matched nothing: it carries the paging fields but no
        # `issues` key. Distinguishable from a bare single issue, which has neither.
        if ($null -eq $issues -and $null -ne $data.PSObject.Properties['total']) {
            return , @()
        }
    }
    # ConvertFrom-Json unwraps a one-element JSON array to a bare object, so a bare list of
    # issues does not necessarily arrive as [System.Array] -- fall through and wrap it.
    if ($null -eq $issues) { $issues = $data }

    # The leading comma stops PowerShell unrolling the array on return: without it an empty
    # result becomes $null and `.Count` throws under Set-StrictMode.
    return , @($issues)
}

<#
Serialises the SARIF graph. -Depth 100 is essential: ConvertTo-Json defaults to depth 2 and would
silently emit "System.Collections.Specialized.OrderedDictionary" strings for anything deeper.
#>
function ConvertTo-SarifJson {
    param($Sarif)
    return $Sarif | ConvertTo-Json -Depth 100
}

# --------------------------------------------------------------------------------------------
# Self-test
# --------------------------------------------------------------------------------------------

function Invoke-SelfTest {
    $failures = [System.Collections.Generic.List[string]]::new()
    $checks = 0

    function Assert-That {
        param([string] $Name, [scriptblock] $Body)
        $script:__checks++
        try {
            $ok = & $Body
            if (-not $ok) { $script:__failures.Add("FAIL: $Name") }
            else { Write-Information "  ok  $Name" -InformationAction Continue }
        } catch {
            $script:__failures.Add("ERROR: $Name -> $($_.Exception.Message)")
        }
    }
    $script:__failures = $failures
    $script:__checks = 0

    $project = 'ZanattaMichael_Chromium-PDF-Editor'

    Assert-That 'strips the project key prefix' {
        (Get-RelativePath -Component "${project}:src/PdfEditor.Core/OcrTool.cs" -ProjectKey $project) -eq 'src/PdfEditor.Core/OcrTool.cs'
    }
    Assert-That 'leaves an already relative path alone' {
        (Get-RelativePath -Component 'src/Foo.cs' -ProjectKey $project) -eq 'src/Foo.cs'
    }
    Assert-That 'falls back to the last segment for an unexpected prefix' {
        (Get-RelativePath -Component 'other:src/Foo.cs' -ProjectKey $project) -eq 'src/Foo.cs'
    }

    Assert-That 'converts 0-based offsets to 1-based columns' {
        # Sonar's startOffset 4 is SARIF's startColumn 5 -- an off-by-one here silently
        # mis-anchors every annotation by one character.
        $r = Get-SarifRegion -Issue ([pscustomobject]@{
            textRange = [pscustomobject]@{ startLine = 42; endLine = 42; startOffset = 4; endOffset = 10 } })
        $r['startLine'] -eq 42 -and $r['endLine'] -eq 42 -and $r['startColumn'] -eq 5 -and $r['endColumn'] -eq 11
    }
    Assert-That 'falls back to the bare line when there is no text range' {
        $r = Get-SarifRegion -Issue ([pscustomobject]@{ line = 7 })
        $r['startLine'] -eq 7 -and -not $r.Contains('startColumn')
    }
    Assert-That 'returns null when there is no position at all' {
        $null -eq (Get-SarifRegion -Issue ([pscustomobject]@{}))
    }
    Assert-That 'drops an end column that would precede the start' {
        $r = Get-SarifRegion -Issue ([pscustomobject]@{
            textRange = [pscustomobject]@{ startLine = 3; endLine = 3; startOffset = 20; endOffset = 2 } })
        -not $r.Contains('endColumn')
    }
    Assert-That 'keeps an earlier end column when the range spans lines' {
        $r = Get-SarifRegion -Issue ([pscustomobject]@{
            textRange = [pscustomobject]@{ startLine = 3; endLine = 5; startOffset = 20; endOffset = 2 } })
        $r['endColumn'] -eq 3
    }

    Assert-That 'maps legacy severities' {
        (Get-SarifLevel ([pscustomobject]@{ severity = 'BLOCKER' })) -eq 'error' -and
        (Get-SarifLevel ([pscustomobject]@{ severity = 'CRITICAL' })) -eq 'error' -and
        (Get-SarifLevel ([pscustomobject]@{ severity = 'MAJOR' })) -eq 'warning' -and
        (Get-SarifLevel ([pscustomobject]@{ severity = 'MINOR' })) -eq 'note' -and
        (Get-SarifLevel ([pscustomobject]@{ severity = 'INFO' })) -eq 'note'
    }
    Assert-That 'prefers the newer impacts shape' {
        (Get-SarifLevel ([pscustomobject]@{
            severity = 'INFO'; impacts = @([pscustomobject]@{ severity = 'HIGH' }) })) -eq 'error'
    }
    Assert-That 'defaults to warning for an unknown severity' {
        (Get-SarifLevel ([pscustomobject]@{ severity = 'WEIRD' })) -eq 'warning'
    }

    $sample = @(
        [pscustomobject]@{
            rule = 'csharpsquid:S1118'; severity = 'MAJOR'; type = 'CODE_SMELL'; hash = 'abc123'
            component = "${project}:src/PdfEditor.Core/OcrTool.cs"; line = 42
            textRange = [pscustomobject]@{ startLine = 42; endLine = 42; startOffset = 4; endOffset = 10 }
            message = 'Add a private constructor.'
        },
        [pscustomobject]@{
            rule = 'javascript:S3776'; severity = 'CRITICAL'; type = 'CODE_SMELL'
            component = "${project}:extension/src/viewer.js"; line = 10
            message = 'Refactor this function.'
        },
        [pscustomobject]@{  # same rule again -- must reuse the rule entry, not duplicate it
            rule = 'csharpsquid:S1118'; severity = 'MAJOR'
            component = "${project}:src/PdfEditor.Core/FormTools.cs"; line = 9
            message = 'Add a private constructor.'
        }
    )
    $sarif = Convert-ToSarif -Issues $sample -ProjectKey $project -Organization 'zanattamichael'

    Assert-That 'produces one run with every result' {
        $sarif['version'] -eq '2.1.0' -and $sarif['runs'].Count -eq 1 -and
        $sarif['runs'][0]['results'].Count -eq 3
    }
    Assert-That 'deduplicates rules and keeps ruleIndex consistent' {
        $rules = $sarif['runs'][0]['tool']['driver']['rules']
        $consistent = $true
        foreach ($r in $sarif['runs'][0]['results']) {
            if ($rules[$r['ruleIndex']]['id'] -ne $r['ruleId']) { $consistent = $false }
        }
        $rules.Count -eq 2 -and $consistent
    }
    Assert-That 'maps locations onto repository-relative paths' {
        $uris = @($sarif['runs'][0]['results'] | ForEach-Object {
            $_['locations'][0]['physicalLocation']['artifactLocation']['uri'] })
        $uris[0] -eq 'src/PdfEditor.Core/OcrTool.cs' -and
        $uris[1] -eq 'extension/src/viewer.js' -and
        $uris[2] -eq 'src/PdfEditor.Core/FormTools.cs'
    }
    Assert-That 'carries a fingerprint only when Sonar supplies a hash' {
        $sarif['runs'][0]['results'][0]['partialFingerprints']['sonarIssueHash'] -eq 'abc123' -and
        -not $sarif['runs'][0]['results'][1].Contains('partialFingerprints')
    }
    Assert-That 'skips findings with nothing to anchor to' {
        $skipped = Convert-ToSarif -ProjectKey $project -Issues @(
            [pscustomobject]@{ rule = 'csharpsquid:S1'; component = '' },
            [pscustomobject]@{ rule = ''; component = "${project}:src/Foo.cs" },
            [pscustomobject]@{ rule = 'csharpsquid:S2'; component = "${project}:src/" }
        )
        $skipped['runs'][0]['results'].Count -eq 0
    }
    Assert-That 'every result has a location GitHub can place' {
        # upload-sarif rejects a result with no physical location, so this must hold for all.
        $ok = $true
        foreach ($r in $sarif['runs'][0]['results']) {
            $loc = $r['locations'][0]['physicalLocation']
            if (-not $loc['artifactLocation']['uri']) { $ok = $false }
            if ($loc['region']['startLine'] -lt 1) { $ok = $false }
        }
        $ok
    }

    # The PowerShell-specific trap: ConvertTo-Json defaults to depth 2, and single-element
    # collections can serialise as a scalar instead of an array. Round-trip and re-check shape.
    $json = ConvertTo-SarifJson -Sarif $sarif
    $round = $json | ConvertFrom-Json
    Assert-That 'round-trips through JSON with runs/results still arrays' {
        $round.runs -is [System.Array] -and $round.runs[0].results -is [System.Array] -and
        $round.runs[0].results.Count -eq 3
    }
    Assert-That 'serialises nested objects rather than type names' {
        $json -notmatch 'System\.Collections' -and
        $round.runs[0].results[0].locations[0].physicalLocation.region.startColumn -eq 5
    }
    Assert-That 'keeps a single-result report as an array' {
        $one = Convert-ToSarif -ProjectKey $project -Issues @(
            [pscustomobject]@{ rule = 'r:1'; component = "${project}:a.cs"; line = 1; message = 'm' })
        $parsed = (ConvertTo-SarifJson -Sarif $one) | ConvertFrom-Json
        $parsed.runs[0].results -is [System.Array] -and $parsed.runs[0].results.Count -eq 1
    }
    Assert-That 'reads a full API response and a bare list alike' {
        (Read-Issues '{"total":1,"issues":[{"rule":"r","component":"c"}]}').Count -eq 1 -and
        (Read-Issues '[{"rule":"r"}]').Count -eq 1 -and
        (Read-Issues '{"total":0}').Count -eq 0
    }

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Information $_ -InformationAction Continue }
        Write-Information "$($failures.Count) of $($script:__checks) checks failed." -InformationAction Continue
        return 1
    }
    Write-Information "All $($script:__checks) checks passed." -InformationAction Continue
    return 0
}

# --------------------------------------------------------------------------------------------

if ($PSCmdlet.ParameterSetName -eq 'SelfTest') {
    exit (Invoke-SelfTest)
}

$raw = if ($Issues -eq '-') { [Console]::In.ReadToEnd() } else { Get-Content -Raw -LiteralPath $Issues }
$sarif = Convert-ToSarif -Issues (Read-Issues $raw) -ProjectKey $ProjectKey -Organization $Organization
$json = ConvertTo-SarifJson -Sarif $sarif

if ($Output -eq '-') {
    Write-Output $json
} else {
    Set-Content -LiteralPath $Output -Value $json -Encoding utf8
}
$count = $sarif['runs'][0]['results'].Count
Write-Information "Wrote $count finding$(if ($count -ne 1) { 's' }) to $Output" -InformationAction Continue
