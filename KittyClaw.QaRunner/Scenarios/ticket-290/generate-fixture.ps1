# Generates deterministic /costs fixtures for the ticket-290 scenario.
# Dates are computed at generation time so the data always falls inside the
# default "last 30 days" range of the costs page.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$today = Get-Date -Format 'yyyy-MM-dd'
$d1 = (Get-Date).AddDays(-1).ToString('yyyy-MM-dd')
$d2 = (Get-Date).AddDays(-2).ToString('yyyy-MM-dd')
$d3 = (Get-Date).AddDays(-3).ToString('yyyy-MM-dd')

function New-Workspace([string]$name) {
    $workspace = Join-Path $root $name
    if (Test-Path $workspace) { Remove-Item -Recurse -Force $workspace }
    New-Item -ItemType Directory -Force (Join-Path $workspace '.agents/channel') | Out-Null
    return $workspace
}

function CostLine([string]$day, [string]$model, [int]$inputTokens, [decimal]$usd, [bool]$estimated, [string]$run) {
    $flag = if ($estimated) { 'true' } else { 'false' }
    return "{""At"":""${day}T12:00:00"",""Agent"":""qa"",""TicketId"":null,""Model"":""$model"",""InputTokens"":$inputTokens,""OutputTokens"":1000,""CacheReadTokens"":0,""CacheWriteTokens"":0,""UsdCost"":$usd,""DurationSeconds"":1,""ExitCode"":0,""CostEstimated"":$flag,""ProjectSlug"":null,""PipelineId"":null,""RunId"":""$run""}"
}

# Priced project: gpt-5.6-sol has a known input rate, so RTK savings get a dollar segment.
$priced = New-Workspace 'workspace-priced'
@(
    CostLine $d2 'gpt-5.6-sol' 240000 1.20 $false 'qa-priced-1'
    CostLine $d1 'gpt-5.6-sol' 160000 0.80 $false 'qa-priced-2'
    CostLine $d1 'gpt-5.6-sol' 120000 0.60 $true  'qa-priced-3'
    CostLine $today 'gpt-5.6-sol' 100000 0.50 $false 'qa-priced-4'
) | Set-Content -Encoding utf8 (Join-Path $priced '.agents/channel/cost-log.jsonl')
@"
{"daily":[{"date":"$d3","commands":3,"input_tokens":90000,"saved_tokens":60000},{"date":"$d1","commands":2,"input_tokens":280000,"saved_tokens":40000},{"date":"$today","commands":4,"input_tokens":100000,"saved_tokens":100000}]}
"@ | Set-Content -Encoding utf8 (Join-Path $priced '.rtk-gain.json')

# Unpriced project: unknown model rate, so RTK savings must render the honest token-only marker.
$unpriced = New-Workspace 'workspace-unpriced'
@(
    CostLine $today 'mystery-local' 50000 0.30 $true 'qa-unpriced-1'
) | Set-Content -Encoding utf8 (Join-Path $unpriced '.agents/channel/cost-log.jsonl')
@"
{"daily":[{"date":"$today","commands":2,"input_tokens":50000,"saved_tokens":50000}]}
"@ | Set-Content -Encoding utf8 (Join-Path $unpriced '.rtk-gain.json')

# Fake rtk binary: replays the workspace-local canned gain ledger.
@'
@echo off
type "%CD%\.rtk-gain.json"
'@ | Set-Content -Encoding ascii (Join-Path $root 'rtk-fake.cmd')

Write-Host "Fixtures generated under $root"
