param(
    [string]$TargetApi
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($TargetApi)) {
    $TargetApi = if ([string]::IsNullOrWhiteSpace($env:KITTYCLAW_API_URL)) {
        'http://localhost:5230'
    } else {
        $env:KITTYCLAW_API_URL
    }
}
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$scenarioPath = Join-Path $PSScriptRoot 'scenario.json'
$mockExecutable = Join-Path $repoRoot 'KittyClaw.ClaudeMock\bin\Debug\net10.0\claude.exe'
$webExecutable = Join-Path $repoRoot 'KittyClaw.Web\bin\Debug\net10.0\KittyClaw.Web.exe'
$runnerProject = Join-Path $repoRoot 'KittyClaw.QaRunner\KittyClaw.QaRunner.csproj'

if (-not (Test-Path -LiteralPath $mockExecutable)) {
    throw "Build KittyClaw.ClaudeMock before running this scenario: $mockExecutable"
}
if (-not (Test-Path -LiteralPath $webExecutable)) {
    throw "Build KittyClaw.Web before running this scenario: $webExecutable"
}

$previousScenario = $env:KITTYCLAW_MOCK_SCENARIO
$previousScenarioDirectory = $env:KITTYCLAW_MOCK_SCENARIOS_DIR
$previousCodexBinary = $env:KITTYCLAW_CODEX_BIN

try {
    $env:KITTYCLAW_MOCK_SCENARIO = 'codex-product-journey'
    $env:KITTYCLAW_MOCK_SCENARIOS_DIR = $PSScriptRoot
    $env:KITTYCLAW_CODEX_BIN = $mockExecutable

    $arguments = @(
        'run', '--project', $runnerProject, '--no-build', '--',
        '--scenario', $scenarioPath,
        '--ticket', '157',
        '--web-exe', $webExecutable
    )
    $arguments += @('--target-api', $TargetApi)

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "QaRunner failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:KITTYCLAW_MOCK_SCENARIO = $previousScenario
    $env:KITTYCLAW_MOCK_SCENARIOS_DIR = $previousScenarioDirectory
    $env:KITTYCLAW_CODEX_BIN = $previousCodexBinary
}
