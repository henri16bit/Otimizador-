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
