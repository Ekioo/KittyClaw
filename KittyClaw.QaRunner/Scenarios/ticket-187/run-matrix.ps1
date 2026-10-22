param(
    [Parameter(Mandatory = $true)][string]$TargetApi,
    [Parameter(Mandatory = $true)][string]$WebExe,
    [int]$Ticket = 187,
    [string]$Output = "ticket-187-journey-report.json"
)

$ErrorActionPreference = "Stop"
$runner = Join-Path $PSScriptRoot "..\..\bin\Debug\net10.0\KittyClaw.QaRunner.exe"
if (-not (Test-Path -LiteralPath $runner)) { throw "Build KittyClaw.QaRunner before running the matrix." }
$scenarios = @("scenario.json", "codex.json", "grok.json", "fallback.json", "no-provider-retry.json")
$results = foreach ($scenario in $scenarios) {
    $text = & $runner --scenario (Join-Path $PSScriptRoot $scenario) --target-api $TargetApi --ticket $Ticket --web-exe $WebExe
    if ($LASTEXITCODE -ne 0) { throw "Scenario $scenario failed with exit code $LASTEXITCODE.`n$text" }
    $text | ConvertFrom-Json
}
$results | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding utf8
$results
