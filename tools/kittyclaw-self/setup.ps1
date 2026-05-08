# Sets up the KittyClaw self-development workspace by copying the e2e qa-tester
# SKILL and sample scenarios into the live KittyClaw workspace
# (%APPDATA%\KittyClaw\projects\todo\.agents\qa-tester\). Re-run after editing
# tools/kittyclaw-self/qa-tester/SKILL.md to propagate.

$ErrorActionPreference = 'Stop'

$src = Resolve-Path (Join-Path $PSScriptRoot 'qa-tester')
$dst = Join-Path $env:APPDATA 'KittyClaw\projects\todo\.agents\qa-tester'

if (-not (Test-Path $dst)) {
    Write-Host "Live workspace does not exist yet: $dst"
    Write-Host "Initialize the 'todo' project in KittyClaw first, then re-run this script."
    exit 1
}

# Copy the SKILL.md (overwrite the embedded-template generic version with the e2e one)
Copy-Item (Join-Path $src 'SKILL.md') (Join-Path $dst 'SKILL.md') -Force
Write-Host "  copied SKILL.md -> $dst"

# Copy sample scenarios
$samplesSrc = Join-Path $src 'sample-scenarios'
$samplesDst = Join-Path $dst 'sample-scenarios'
New-Item -ItemType Directory -Force -Path $samplesDst | Out-Null
Get-ChildItem $samplesSrc -Filter '*.json' | ForEach-Object {
    Copy-Item $_.FullName $samplesDst -Force
    Write-Host ("  copied scenario {0}" -f $_.Name)
}

Write-Host ""
Write-Host "Done. The next qa-on-review dispatch will use the e2e SKILL." -ForegroundColor Green
