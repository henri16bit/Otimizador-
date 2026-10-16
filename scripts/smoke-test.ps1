[CmdletBinding()]
param([string]$ReleaseDirectory = '', [int]$TimeoutSeconds = 60)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'O teste da janela WPF exige Windows.' }
$repo = Split-Path -Parent $PSScriptRoot
if (!$ReleaseDirectory) { $ReleaseDirectory = Join-Path $repo 'artifacts/windows-x64' }
$executable = Join-Path $ReleaseDirectory 'PCAnalyzer.exe'
$output = Join-Path $repo ('artifacts/startup-smoke-' + [Guid]::NewGuid().ToString('N') + '.json')
$directory = Split-Path -Parent $output
[void][IO.Directory]::CreateDirectory($directory)
$process = Start-Process -FilePath $executable -WorkingDirectory $ReleaseDirectory -ArgumentList @('--smoke-test', '--smoke-output', ('"' + $output + '"')) -PassThru
try {
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw 'A janela WPF não concluiu o teste dentro do limite.'
    }
    if ($process.ExitCode -ne 0) { throw "A janela WPF encerrou com código $($process.ExitCode). Consulte o resultado de inicialização." }
    if (!(Test-Path -LiteralPath $output)) { throw 'O aplicativo não gravou o resultado do teste da janela.' }
    $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if (!$result.success -or $result.views -lt 10 -or $result.detailViews -lt 7 -or !$result.compactLayoutChecked -or $result.passiveHardwareStarted) {
        throw 'O aplicativo não confirmou as dez páginas, sete abas internas e o tamanho compacto sem iniciar coleta de hardware.'
    }
    Write-Host "Janela WPF validada: $($result.views) páginas, $($result.detailViews) abas internas, tamanho compacto; saída 0."
} finally { $process.Dispose() }
