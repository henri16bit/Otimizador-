# PC Performance Analyzer v0.4.0-preview.3

**ANALISAR MEU PC** coordena observação, rede e fontes opcionais em um relatório: o que foi medido, o que merece atenção, o que permanece inconclusivo e qual o próximo passo.

- Menu simples com Painel, Analisar, Resultados, Otimizações, Avançado e Configurações. Ferramentas técnicas continuam disponíveis.
- CPU/RAM e processos começam sem esperar todo o inventário. Fontes opcionais têm recuperação limitada; pausa, atraso e falha ficam visíveis.
- Rede identifica a conexão e lê driver/propriedades automaticamente. A análise compara erros no intervalo da mesma conexão.
- Jogos permitem configurar PresentMon assinado, selecionar um processo ou aguardar sua abertura. Capturas e testes ativos continuam opcionais.
- ETW distingue DPC, ISR, eventos auxiliares, rejeitados e analisados. Cancelamentos e falhas de leitura preservam dados disponíveis.
- Relatórios parciais, processos, evidências e próximos passos ficam no histórico. Importações não alteram leituras atuais. Falhas não mantêm indicadores ou exportações do resultado anterior.

Baixe **PCAnalyzer-windows-x64.zip** em **Assets**. Os arquivos “Source code” contêm apenas código-fonte.

1. Extraia o ZIP completo para uma pasta gravável.
2. Abra **PCAnalyzer.exe**, mantendo DLLs e subpastas junto dele.
3. Use **ANALISAR MEU PC**. Em Analisar, escolha duração e testes opcionais.

O pacote portátil inclui o runtime .NET. Inicie como usuário normal; o auxiliar solicita UAC quando necessário. Ajustes mantêm prévia, confirmação, backup, releitura e restauração.

A publicação exige compilação sem avisos, testes de lógica/empacotamento, cenários WPF e verificação passiva real de CPU/RAM/processos no runner Windows. Isso não substitui validação de GPU, ETW, PresentMon, ajustes e Windows 10 Pro 22H2 no computador alvo. Benchmark GPU, SMART completo, sensores térmicos CPU e testes de carga mais amplos continuam limitados; não são estimados.

O executável do PC Analyzer ainda não é assinado. Se a abertura falhar, consulte `%LOCALAPPDATA%\PCAnalyzer\logs\startup-last-error.txt`. Confira o download com `Get-FileHash .\PCAnalyzer-windows-x64.zip -Algorithm SHA256` e compare com o arquivo `.sha256`.
