# Isolated regression checks; mock gh never contacts GitHub or changes a real release.
$ErrorActionPreference = 'Stop'
$publish = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../scripts/publish-release.ps1'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pcanalyzer-release-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $temporary 'artifacts'))
$original = Get-Location
$global:pcanalyzerReleaseTestState = $null
function global:gh {
    $command = @($args)
    $global:pcanalyzerReleaseTestState.calls.Add(($command -join ' '))
    $global:LASTEXITCODE = 0
    switch ($command[1]) {
        'view' {
            if (!$global:pcanalyzerReleaseTestState.exists) { $global:LASTEXITCODE = 1; return 'Release not found' }
            return (@{ isDraft = $global:pcanalyzerReleaseTestState.draft; assets = @($global:pcanalyzerReleaseTestState.assets); url = 'https://example.invalid/release' } | ConvertTo-Json -Depth 5 -Compress)
        }
        'create' { $global:pcanalyzerReleaseTestState.exists = $true; $global:pcanalyzerReleaseTestState.draft = $true }
        'upload' {
            if ($global:pcanalyzerReleaseTestState.failUpload) { $global:LASTEXITCODE = 1; return 'Upload failed' }
            $global:pcanalyzerReleaseTestState.assets = @(@{name='PCAnalyzer-windows-x64.zip';size=10}, @{name='PCAnalyzer-windows-x64.zip.sha256';size=20})
        }
        'edit' { $global:pcanalyzerReleaseTestState.draft = $false }
        default { throw 'Unexpected gh invocation in regression check.' }
    }
}
function Reset-Case([bool]$Exists, [bool]$Draft, [object[]]$Assets = @(), [bool]$FailUpload = $false) {
    $global:pcanalyzerReleaseTestState = @{ exists=$Exists; draft=$Draft; assets=$Assets; failUpload=$FailUpload; calls=[Collections.Generic.List[string]]::new() }
    if (Test-Path artifacts/release-publication.json) { Remove-Item -LiteralPath artifacts/release-publication.json }
}
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
try {
    Set-Location $temporary
    'test ZIP' | Set-Content artifacts/PCAnalyzer-windows-x64.zip
    'test hash' | Set-Content artifacts/PCAnalyzer-windows-x64.zip.sha256

    Reset-Case $true $false
    & $publish -Tag v0.1.0-preview.2
    Assert (Test-Path artifacts/release-publication.json) 'Source-only public release must receive both assets.'
    Assert (@($global:pcanalyzerReleaseTestState.calls | Where-Object { $_ -match '^release upload' }).Count -eq 1) 'Exactly one upload expected.'
    Assert (@($global:pcanalyzerReleaseTestState.calls | Where-Object { $_ -match '--clobber|^release edit' }).Count -eq 0) 'Public release must not be overwritten or republished.'

    Reset-Case $false $false
    & $publish -Tag v0.1.0-preview.2
    $calls = $global:pcanalyzerReleaseTestState.calls.ToArray()
    Assert ([Array]::FindIndex($calls, [Predicate[string]]{param($c) $c -match '^release upload'}) -lt [Array]::FindIndex($calls, [Predicate[string]]{param($c) $c -match '^release edit'})) 'Upload must finish before publication.'
    Assert (!$global:pcanalyzerReleaseTestState.draft) 'New draft must become public only after upload.'

    Reset-Case $true $false @(@{name='PCAnalyzer-windows-x64.zip';size=10}, @{name='PCAnalyzer-windows-x64.zip.sha256';size=20})
    & $publish -Tag v0.1.0-preview.2
    Assert (@($global:pcanalyzerReleaseTestState.calls | Where-Object { $_ -match '^release upload|^release edit|^release create' }).Count -eq 0) 'Complete published assets must be preserved.'

    Reset-Case $true $false @(@{name='PCAnalyzer-windows-x64.zip';size=10})
    $rejected = $false
    try { & $publish -Tag v0.1.0-preview.2 } catch { $rejected = $_.Exception.Message -match 'parte do pacote' }
    Assert $rejected 'Partially published assets must not be mixed across builds.'
    Assert (@($global:pcanalyzerReleaseTestState.calls | Where-Object { $_ -match '^release upload' }).Count -eq 0) 'Partial public assets must remain untouched.'

    Reset-Case $true $true @() $true
    $rejected = $false
    try { & $publish -Tag v0.1.0-preview.2 } catch { $rejected = $_.Exception.Message -match 'enviar' }
    Assert $rejected 'Upload failure must abort publication.'
    Assert (@($global:pcanalyzerReleaseTestState.calls | Where-Object { $_ -match '^release edit' }).Count -eq 0) 'Failed upload must never publish the draft.'
    Write-Host 'Five isolated release publication checks passed.'
} finally {
    Set-Location $original
    Remove-Item Function:\gh
    Remove-Variable -Name pcanalyzerReleaseTestState -Scope Global
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
