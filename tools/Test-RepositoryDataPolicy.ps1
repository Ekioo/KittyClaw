#!/usr/bin/env pwsh
[CmdletBinding()]
param([switch] $SelfTest)

$ErrorActionPreference = 'Stop'
$repo = (& git rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $repo) { throw 'Run this script inside a Git worktree.' }

function Test-ContentPolicy {
    param([string] $Path, [string] $Content)
    $findings = [System.Collections.Generic.List[string]]::new()
    $normalized = $Path.Replace('\', '/')
    $isDataFile = $normalized -match '(?i)(^|/)(Fixtures|Scenarios)/' -or $normalized -match '(?i)\.(json|csv|tsv|ya?ml)$'

    if ($normalized -match '(?i)^evidence/') { $findings.Add('execution evidence path') }
    if ($isDataFile -and $Content -match '(?im)(?:[A-Z]:[\\/](?:Users|Sources)[\\/]|/(?:home|Users)/[^/\s]+/|Sources\.worktrees[\\/])') {
        $findings.Add('absolute user or worktree path')
    }
    $emails = [regex]::Matches($Content, '(?i)(?<![\w.+-])([\w.+-]+@(?:[\w-]+\.)+[A-Za-z]{2,})(?![\w.-])')
    foreach ($match in $emails) {
        if ($isDataFile -and $match.Value -notmatch '(?i)@(example\.(?:com|org|net)|kittyclaw\.local)$') {
            $findings.Add("non-synthetic email address: $($match.Value)")
        }
    }
    $secretPatterns = @(
        '(?i)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
        '(?i)\b(?:gh[pousr]_[A-Za-z0-9]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})\b',
        '(?i)\bAuthorization\s*[:=]\s*Bearer\s+[A-Za-z0-9._~+/=-]{20,}'
    )
    foreach ($pattern in $secretPatterns) {
        if ($normalized -ne 'tools/Test-RepositoryDataPolicy.ps1' -and $Content -match $pattern) {
            $findings.Add('known secret signature')
            break
        }
    }
    if ($isDataFile -and $normalized -ne 'tools/Test-RepositoryDataPolicy.ps1' -and
        $Content -match '(?is)"tickets"\s*:\s*\[' -and
        $Content -match '(?is)"(?:columns|activities|comments)"\s*:\s*\[' -and
        $Content -match '(?is)"(?:title|status|assignedTo|createdBy)"\s*:') {
        $findings.Add('board export structure')
    }
    return $findings
}

if ($SelfTest) {
    $cases = @(
        @{ Name='synthetic fixture'; Path='KittyClaw.Core.Tests/Fixtures/user.json'; Content='{"email":"qa@example.com","path":"fixtures/project"}'; Valid=$true },
        @{ Name='evidence path'; Path='evidence/run.json'; Content='{}'; Valid=$false },
        @{ Name='real email'; Path='KittyClaw.Core.Tests/Fixtures/user.json'; Content='{"email":"person@private-company.test"}'; Valid=$false },
        @{ Name='absolute path'; Path='KittyClaw.Core.Tests/Fixtures/user.json'; Content='{"path":"C:/Sources/KittyClaw/Sources.worktrees/ticket-42"}'; Valid=$false },
        @{ Name='board export'; Path='KittyClaw.Core.Tests/Fixtures/board.json'; Content='{"tickets":[{"title":"Private","status":"Todo"}],"columns":[]}'; Valid=$false },
        @{ Name='secret'; Path='KittyClaw.Core.Tests/Fixtures/auth.txt'; Content='Authorization: Bearer abcdefghijklmnopqrstuvwxyz123456'; Valid=$false }
    )
    foreach ($case in $cases) {
        $valid = (Test-ContentPolicy $case.Path $case.Content).Count -eq 0
        if ($valid -ne $case.Valid) { throw "Self-test failed: $($case.Name)" }
    }
    Write-Host "Repository data-policy self-tests passed ($($cases.Count) cases)."
}

$failures = [System.Collections.Generic.List[string]]::new()
$tracked = & git -C $repo ls-files
foreach ($relative in $tracked) {
    $full = Join-Path $repo $relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
    $info = Get-Item -LiteralPath $full
    if ($info.Length -gt 2MB) { continue }
    try { $content = [System.IO.File]::ReadAllText($full) } catch { continue }
    foreach ($finding in (Test-ContentPolicy $relative $content)) {
        $failures.Add("${relative}: $finding")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Error "Repository data policy failed with $($failures.Count) finding(s)."
    exit 1
}

Write-Host "Repository data policy passed ($($tracked.Count) tracked files scanned)."
