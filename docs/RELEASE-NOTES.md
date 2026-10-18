# PC Performance Analyzer v0.5.0-preview.2

**ANALISAR MEU PC** coordena observação, rede e fontes opcionais em um relatório: o que foi medido, o que merece atenção, o que permanece inconclusivo e qual o próximo passo.

- Versão exclusivamente de auditoria e consultoria: Recomendações substitui Otimizações. Aplicação/restauração de ajustes está bloqueada também na API e no auxiliar.
- Catálogo extensível com leituras de Registro/MMCSS/IFEO/energia/mouse, processos/serviços, NVIDIA DRS, MSI/IRQ/afinidade/USB, rede e armazenamento. Estado configurado não é tratado como benefício comprovado.
- Resultados na própria tela, cobertura por área, detalhes técnicos recolhidos, snapshots persistidos e comparação de configurações.
- Teste manual adicional de memória: leitura/escrita/cópia/acesso dependente em buffers virtuais; não mede DRAM isoladamente.
- CPU/RAM e processos começam sem esperar todo o inventário. Fontes opcionais têm recuperação limitada; pausa, atraso e falha ficam visíveis.
- Rede identifica a conexão e lê driver/propriedades automaticamente. A análise compara erros no intervalo da mesma conexão.
- Jogos permitem configurar PresentMon assinado, selecionar um processo ou aguardar sua abertura. Capturas e testes ativos continuam opcionais.
- ETW distingue DPC, ISR, eventos auxiliares, rejeitados e analisados. Cancelamentos e falhas de leitura preservam dados disponíveis.
- Relatórios parciais, processos, evidências e próximos passos ficam no histórico. Importações não alteram leituras atuais. Falhas não mantêm indicadores ou exportações do resultado anterior.

Baixe **PCAnalyzer-windows-x64.zip** em **Assets**. Os arquivos “Source code” contêm apenas código-fonte.

1. Extraia o ZIP completo para uma pasta gravável.
2. Abra **PCAnalyzer.exe**, mantendo DLLs e subpastas junto dele.
3. Use **ANALISAR MEU PC**. Em Analisar, escolha duração e testes opcionais.

O pacote portátil inclui o runtime .NET. Extraia tudo em uma pasta nova e inicie `PCAnalyzer.exe` como usuário normal. O auxiliar solicita UAC somente para captura DPC/ISR autorizada. Nenhuma configuração operacional é alterada. Perfis NVIDIA e dispositivos físicos ainda exigem validação no computador alvo; consulte `docs/PERFORMANCE-CONSULTANCY.md`.

A publicação exige compilação sem avisos, testes de lógica/empacotamento, cenários WPF e verificação passiva real de CPU/RAM/processos no runner Windows. Isso não substitui validação de GPU, ETW, PresentMon, leituras dos dispositivos e Windows 10 Pro 22H2 no computador alvo. Benchmark GPU, SMART completo, sensores térmicos CPU e testes de carga mais amplos continuam limitados; não são estimados.

O executável do PC Analyzer ainda não é assinado. Se a abertura falhar, consulte `%LOCALAPPDATA%\PCAnalyzer\logs\startup-last-error.txt`. Confira o download com `Get-FileHash .\PCAnalyzer-windows-x64.zip -Algorithm SHA256` e compare com o arquivo `.sha256`.
