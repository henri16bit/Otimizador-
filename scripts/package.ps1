[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReleaseDirectory, [string]$ZipPath = '')
$ErrorActionPreference = 'Stop'
$release = [System.IO.Path]::GetFullPath($ReleaseDirectory)
if (!$ZipPath) { $ZipPath = Join-Path (Split-Path -Parent $release) 'PCAnalyzer-windows-x64.zip' }
$ZipPath = [System.IO.Path]::GetFullPath($ZipPath)
if ($ZipPath.StartsWith($release + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'O ZIP deve ficar fora da pasta publicada.'
}
$executables = @('PCAnalyzer.exe','worker/PCAnalyzer.Worker.exe','verify/PCAnalyzer.Verify.exe')
$runtimeConfigs = @('PCAnalyzer.runtimeconfig.json','worker/PCAnalyzer.Worker.runtimeconfig.json','verify/PCAnalyzer.Verify.runtimeconfig.json')
$required = $executables + $runtimeConfigs + @('e_sqlite3.dll','verify/e_sqlite3.dll','README.md','THIRD-PARTY-NOTICES.md','docs/VALIDATION.md')
foreach ($relative in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $release $relative) -PathType Leaf)) { throw "Arquivo obrigatório ausente: $relative" }
}
foreach ($relative in $executables) {
    $stream = [IO.File]::OpenRead((Join-Path $release $relative))
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Executável PE inválido: $relative" }
        $stream.Position = 0x3C
        $offset = $reader.ReadUInt32()
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw "Executável não é Windows x64: $relative" }
    } finally { $reader.Dispose() }
}
foreach ($relative in $runtimeConfigs) {
    $config = Get-Content -LiteralPath (Join-Path $release $relative) -Raw | ConvertFrom-Json
    $frameworks = @($config.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
    if ($frameworks -notcontains 'Microsoft.NETCore.App') { throw "Runtime autossuficiente ausente: $relative" }
    if ($relative -eq $runtimeConfigs[0] -and $frameworks -notcontains 'Microsoft.WindowsDesktop.App') { throw 'Runtime WPF ausente.' }
}
$manifest = [ordered]@{}
$items = Get-ChildItem -LiteralPath $release -Recurse -Force
if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'O pacote não aceita links simbólicos ou junctions.'
}
$files = $items | Where-Object { !$_.PSIsContainer }
foreach ($file in $files | Sort-Object FullName) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Link simbólico não permitido: $($file.FullName)" }
    $relative = [IO.Path]::GetRelativePath($release, $file.FullName).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
    if ($relative -eq 'SHA256SUMS.json') { continue }
    $manifest[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.json') -Encoding utf8
$temporary = Join-Path (Split-Path -Parent $ZipPath) (([IO.Path]::GetFileName($ZipPath)) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    # ZipFile preserves all file names; Compress-Archive may omit hidden files.
    [IO.Compression.ZipFile]::CreateFromDirectory($release, $temporary, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        foreach ($entry in $archive.Entries) {
            if (!$entry.Name -or $entry.FullName -eq 'SHA256SUMS.json') { continue }
            if (!$manifest.Contains($entry.FullName)) { throw "Entrada inesperada no ZIP: $($entry.FullName)" }
            $stream = $entry.Open()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $actual = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant()
                if ($actual -ne $manifest[$entry.FullName]) { throw "SHA256 do ZIP diverge: $($entry.FullName)" }
            } finally { $algorithm.Dispose(); $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $ZipPath -Force
    $digest = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  $([IO.Path]::GetFileName($ZipPath))" | Set-Content -LiteralPath ($ZipPath + '.sha256') -Encoding ascii
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Write-Host "Pacote verificado: $ZipPath"
