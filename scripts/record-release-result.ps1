# Git notes expose only this publication's results when the cloud cannot query the GitHub API.
$ErrorActionPreference = 'Stop'
$steps = $env:RELEASE_STEPS | ConvertFrom-Json
$result = [ordered]@{
    tag = $env:RELEASE_TAG
    timestamp = [DateTimeOffset]::UtcNow.ToString('O')
    runUrl = $env:RELEASE_RUN_URL
    steps = [ordered]@{}
}
foreach ($property in $steps.PSObject.Properties) {
    $result.steps[$property.Name] = $property.Value.outcome
}
$smoke = Get-ChildItem artifacts/startup-smoke-*.json -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($smoke) { $result['startup'] = Get-Content -LiteralPath $smoke.FullName -Raw | ConvertFrom-Json }
if (Test-Path -LiteralPath artifacts/ui-flow-result.json) {
    $result['uiFlows'] = Get-Content -LiteralPath artifacts/ui-flow-result.json -Raw | ConvertFrom-Json
}
if (Test-Path -LiteralPath artifacts/ui-flow-startup.json) {
    $result['uiFlowStartup'] = Get-Content -LiteralPath artifacts/ui-flow-startup.json -Raw | ConvertFrom-Json
}
$hardware = Get-ChildItem artifacts/windows-verification -Filter validation-result.json -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($hardware) { $result['hardware'] = Get-Content -LiteralPath $hardware.FullName -Raw | ConvertFrom-Json }
$testRun = Get-ChildItem tests/PCAnalyzer.Tests/TestResults -Filter '*.trx' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($testRun) {
    [xml]$testXml = Get-Content -LiteralPath $testRun.FullName -Raw
    $counts = $testXml.TestRun.ResultSummary.Counters
    $result['tests'] = @{ total = [int]$counts.total; passed = [int]$counts.passed; failed = [int]$counts.failed; notExecuted = [int]$counts.notExecuted }
}
if ($steps.ui_flows.outcome -ne 'success') {
    foreach ($stream in @('stdout', 'stderr')) {
        $logPath = "artifacts/ui-flow-$stream.txt"
        if (Test-Path -LiteralPath $logPath) { $result["uiFlow$stream"] = @(Get-Content -LiteralPath $logPath -Tail 60) }
    }
}
if (Test-Path -LiteralPath artifacts/release-publication.json) {
    $result['publication'] = Get-Content -LiteralPath artifacts/release-publication.json -Raw | ConvertFrom-Json
}
$file = Join-Path $env:RUNNER_TEMP 'pcanalyzer-release-result.json'
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $file -Encoding utf8
$remote = git ls-remote origin refs/notes/pcanalyzer-releases
if ($LASTEXITCODE) { throw 'Não foi possível consultar o registro Git da publicação.' }
if ($remote) {
    git fetch --no-tags origin refs/notes/pcanalyzer-releases:refs/notes/pcanalyzer-releases
    if ($LASTEXITCODE) { throw 'Não foi possível obter o registro Git da publicação.' }
}
git -c user.name=github-actions -c user.email=41898282+github-actions[bot]@users.noreply.github.com notes --ref=pcanalyzer-releases add -f -F $file HEAD
if ($LASTEXITCODE) { throw 'Não foi possível registrar o resultado.' }
git push origin refs/notes/pcanalyzer-releases
if ($LASTEXITCODE) { throw 'Não foi possível enviar o resultado Git.' }
$previews = @{
    'pcanalyzer-ui-preview' = 'artifacts/startup-smoke-*-overview.png'
    'pcanalyzer-network-preview' = 'artifacts/startup-smoke-*-investigar.png'
    'pcanalyzer-flow-preview' = 'artifacts/ui-flow-screenshot-dashboard-fixture.png'
    'pcanalyzer-network-fixture-preview' = 'artifacts/ui-flow-screenshot-network-fixture.png'
    'pcanalyzer-analysis-preview' = 'artifacts/ui-flow-screenshot-analysis-fixture.png'
    'pcanalyzer-report-preview' = 'artifacts/ui-flow-screenshot-report-fixture.png'
    'pcanalyzer-audit-preview' = 'artifacts/ui-flow-screenshot-audit-1366.png'
}
foreach ($name in $previews.Keys) {
    $preview = Get-ChildItem $previews[$name] -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $preview) { continue }
    $previewRef = 'refs/notes/' + $name
    $remote = git ls-remote origin $previewRef
    if ($LASTEXITCODE) { throw 'Não foi possível consultar a prévia da interface.' }
    if ($remote) {
        git fetch --no-tags origin ($previewRef + ':' + $previewRef)
        if ($LASTEXITCODE) { throw 'Não foi possível obter a prévia da interface.' }
    }
    $blob = git hash-object -w -- $preview.FullName
    if ($LASTEXITCODE) { throw 'Não foi possível preparar a captura da interface.' }
    git -c user.name=github-actions -c user.email=41898282+github-actions[bot]@users.noreply.github.com notes --ref=$name add -f -C $blob HEAD
    if ($LASTEXITCODE) { throw 'Não foi possível registrar a captura da interface.' }
    git push origin $previewRef
    if ($LASTEXITCODE) { throw 'Não foi possível enviar a captura da interface.' }
}
