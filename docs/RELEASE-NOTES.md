# PC Performance Analyzer — prévia para Windows x64

Baixe **PCAnalyzer-windows-x64.zip** na seção **Assets** abaixo. Os arquivos automáticos “Source code” contêm apenas o código-fonte.

1. Extraia o ZIP completo para uma pasta gravável.
2. Abra **PCAnalyzer.exe** na pasta extraída.
3. Mantenha as DLLs e as subpastas `worker`, `verify` e `docs` junto ao aplicativo.

O pacote é portátil e inclui o runtime .NET. A abertura inicia coleta passiva; operações privilegiadas solicitadas pelo usuário pedem autorização pelo UAC.

Esta é uma prévia: a execução da interface e as integrações no computador Windows ainda precisam de validação local. O Windows 10 Pro 22H2 solicitado é um alvo de compatibilidade técnica pendente; consulte o README sobre o suporte do .NET 10. O executável ainda não tem assinatura digital.

O processo de publicação exige compilação sem avisos, testes automatizados e verificação do conteúdo do ZIP. O arquivo `.sha256` permite conferir o download com `Get-FileHash .\PCAnalyzer-windows-x64.zip -Algorithm SHA256` no PowerShell. Consulte `docs/WINDOWS-VALIDATION.md` no pacote para a validação local.
