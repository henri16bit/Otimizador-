[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Tag)
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v[0-9][0-9A-Za-z.+_-]{0,100}$') { throw 'Tag de versão inválida.' }
$assets = @('artifacts/PCAnalyzer-windows-x64.zip', 'artifacts/PCAnalyzer-windows-x64.zip.sha256')
foreach ($asset in $assets) {
    if (!(Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Pacote ausente: $asset" }
}
$view = gh release view $Tag --json isDraft,assets,url 2>&1
if ($LASTEXITCODE -ne 0) {
    gh release create $Tag --verify-tag --draft --prerelease --title "PC Performance Analyzer $Tag" --notes-file docs/RELEASE-NOTES.md
    if ($LASTEXITCODE) { throw 'Falha ao criar o rascunho da versão.' }
    $view = gh release view $Tag --json isDraft,assets,url
    if ($LASTEXITCODE) { throw 'Falha ao consultar o rascunho da versão.' }
}
$release = ($view -join "`n") | ConvertFrom-Json
$names = @($release.assets | ForEach-Object { $_.name })
$expected = @($assets | ForEach-Object { Split-Path -Leaf $_ })
$existing = @($expected | Where-Object { $_ -in $names })
if (!$release.isDraft -and $existing.Count -eq $expected.Count) {
    Write-Host 'A versão já possui os dois arquivos; eles serão preservados.'
} else {
    if (!$release.isDraft -and $existing.Count) {
        throw 'A versão pública possui apenas parte do pacote. Use uma nova tag para evitar misturar arquivos de compilações diferentes.'
    }
    # A tag page / manually created release may contain only the automatic source archives.
    # Attach our binaries there too, without overwriting any published application asset.
    $arguments = @('release', 'upload', $Tag) + $assets
    if ($release.isDraft) { $arguments += '--clobber' }
    & gh @arguments
    if ($LASTEXITCODE) { throw 'Falha ao enviar os arquivos da versão.' }
    if ($release.isDraft) {
        gh release edit $Tag --draft=false --prerelease
        if ($LASTEXITCODE) { throw 'Falha ao publicar a versão.' }
    }
}
$view = gh release view $Tag --json isDraft,assets,url
if ($LASTEXITCODE) { throw 'Falha ao verificar os arquivos publicados.' }
$release = ($view -join "`n") | ConvertFrom-Json
if ($release.isDraft) { throw 'A versão ainda está em rascunho.' }
foreach ($name in $expected) {
    $asset = @($release.assets | Where-Object { $_.name -eq $name })
    if ($asset.Count -ne 1 -or $asset[0].size -le 0) { throw "Arquivo não confirmado no GitHub: $name" }
}
$release | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath artifacts/release-publication.json -Encoding utf8
Write-Host "Download confirmado: $($release.url)"
