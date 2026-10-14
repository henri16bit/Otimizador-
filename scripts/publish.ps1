[CmdletBinding()]
param([string]$OutputDirectory = "")
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/windows-x64' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
Push-Location $repo
try {
    & dotnet restore PCAnalyzer.sln --locked-mode
    if ($LASTEXITCODE) { throw 'Restore falhou.' }
    & dotnet test tests/PCAnalyzer.Tests/PCAnalyzer.Tests.csproj -c Release
    if ($LASTEXITCODE) { throw 'Testes falharam.' }
    & dotnet publish src/PCAnalyzer.App/PCAnalyzer.App.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:RestoreLockedMode=true -o $OutputDirectory
    if ($LASTEXITCODE) { throw 'Publicação da interface falhou.' }
    & dotnet publish src/PCAnalyzer.Worker/PCAnalyzer.Worker.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:RestoreLockedMode=true -o (Join-Path $OutputDirectory 'worker')
    if ($LASTEXITCODE) { throw 'Publicação do auxiliar falhou.' }
    & dotnet publish src/PCAnalyzer.Verify/PCAnalyzer.Verify.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:RestoreLockedMode=true -o (Join-Path $OutputDirectory 'verify')
    if ($LASTEXITCODE) { throw 'Publicação do verificador falhou.' }
    Copy-Item README.md -Destination $OutputDirectory
    Copy-Item THIRD-PARTY-NOTICES.md -Destination $OutputDirectory
    Copy-Item docs -Destination $OutputDirectory -Recurse -Force
    & (Join-Path $PSScriptRoot 'package.ps1') -ReleaseDirectory $OutputDirectory
    Write-Host "Publicação Windows x64: $OutputDirectory/PCAnalyzer.exe"
    Write-Host 'Execute como usuário normal; ações privilegiadas solicitam UAC separadamente.'
} finally { Pop-Location }
