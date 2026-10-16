[CmdletBinding()]
param([int]$TimeoutSeconds = 90)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Os cenários WPF exigem Windows.' }
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts'
[void][IO.Directory]::CreateDirectory($artifacts)
$process = Start-Process -FilePath dotnet -WorkingDirectory $repo -PassThru -ArgumentList @(
    'run', '--project', 'tests/PCAnalyzer.WpfHarness/PCAnalyzer.WpfHarness.csproj',
    '-c', 'Release', '--no-build', '--no-restore', '--', '--smoke-test',
    '--smoke-output', 'artifacts/ui-flow-startup.json'
) -RedirectStandardOutput (Join-Path $artifacts 'ui-flow-stdout.txt') -RedirectStandardError (Join-Path $artifacts 'ui-flow-stderr.txt')
try {
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw 'Os cenários WPF excederam o limite. Consulte os registros ui-flow nos artefatos.'
    }
    if ($process.ExitCode -ne 0) { throw "Os cenários WPF encerraram com código $($process.ExitCode)." }
    $result = Get-Content -LiteralPath (Join-Path $artifacts 'ui-flow-result.json') -Raw | ConvertFrom-Json
    if (!$result.success -or $result.checks.Count -lt 20 -or !$result.fixtureDataOnly -or $result.hardwareOrConfigurationChanged) {
        throw 'Os cenários WPF não confirmaram todos os fluxos sem alterações reais de sistema.'
    }
    Write-Host "$($result.checks.Count) cenários WPF aprovados."
} finally { $process.Dispose() }
