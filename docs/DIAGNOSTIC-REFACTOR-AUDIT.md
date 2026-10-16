# Auditoria da análise integrada — 15/10/2026

Base: `f20e5fd`, executável `v0.3.0-preview.3`. Inspeção real anterior às alterações: solução, WPF, ViewModels, backend, coletores Windows/NVML, ETW, PresentMon, rede, regras, armazenamento, relatórios, execução e cancelamento. A validação anterior de interface usa fixtures; não prova leitura do hardware do usuário.

| Área | Classificação da base | Evidência e causa |
|---|---|---|
| Inicialização e fontes básicas | Com defeito | `MainViewModel.InitializeCoreAsync` aguarda todo inventário WMI antes de iniciar a coleta. Consultas seriais lentas bloqueiam também CPU/RAM nativas, processos e rede. |
| Saúde da telemetria | Com defeito | `CollectionStateLabel` depende de inventário/pausa, sem verificar último lote válido, atraso ou erro. `WindowsTelemetryCollector` conserva falhas de inicialização de contadores/NVML até encerrar. |
| Processos | Parcialmente funcional | Uma falha ao ler StartTime/TotalProcessorTime descarta o processo inteiro, mesmo quando nome/PID/memória são acessíveis. A interface não distingue histórico, falha e primeira amostra. |
| CPU/RAM/rede | Implementada, mas não validada no computador alvo | Há APIs nativas para métricas básicas, deltas temporais e contadores localizados. Bloqueio de inicialização e disponibilidade precisam ser corrigidos; números não devem ser inventados. |
| GPU | Parcialmente funcional | NVML real, limitado a NVIDIA suportada. Falta provedor alternativo para utilização quando NVML está ausente; temperatura/VRAM não podem ser inferidas de outra métrica. |
| Execução/histórico | Funcional e validada nos cenários existentes | Estados, cancelamento, SQLite v2, reabertura, exportação e migração cobertos. Não há sessão geral coordenando todos os módulos. |
| Escopo dos resultados | Com defeito | `RunTestAsync` acrescenta triagem CPU/RAM a qualquer teste. `RecordFailure` limpa séries mas mantém indicadores, achados e exportação anteriores; prévias compartilham estado textual. |
| Análise geral | Não implementada | O teste `system` observa 15 s e aplica regras CPU/RAM. Não coordena rede, permissões, latência opcional e relatório por componente. |
| ETW | Parcialmente funcional | Captura/importação reais; `EventsSeen` inclui auxiliares sem contagem separada. Eventos inválidos não têm contagem própria e falha de decodificação pode descartar resumo parcial. |
| Interpretação DPC/frames | Parcialmente funcional | Regras têm recorrência e limites, mas precisam explicitar cobertura/perdas e ajustar confiança em capturas incompletas. Duração isolada não prova latência física ou culpa do driver. |
| PresentMon | Implementada, mas não validada no computador alvo | Captura por PID, sessão exclusiva, parser e cleanup existem. Cancelamento/erro com CSV parcial não chega integralmente ao resultado tipado. Configuração não é persistida nem há fluxo simples integrado. |
| Rede | Parcialmente funcional | Consulta por adaptador e ping existem; o fluxo exige descoberta/leitura manual e falta investigação automática integrada. |
| Alterações reversíveis | Funcional e validada em operações isoladas | Prévia vinculada ao original, backup antes da escrita, confirmação, UAC, releitura/restauração. Aplicação real no driver do usuário segue não validada. |
| Interface simples | Não implementada | Dashboard centrado em ferramentas/cards e navegação técnica; falta ação principal, síntese por área e próximos passos ligados ao relatório. |

## Ordem de implementação

1. Desacoplar coleta básica do inventário; recuperar fontes de forma limitada, identificar atraso/erro/pausa e preservar dados legíveis de processos.
2. Criar painel simples com ação principal e modo avançado preservado.
3. Coordenar preparação, observação, rede, capturas opcionais, interpretação e persistência numa única sessão cancelável.
4. Produzir avaliações por componente, evidências, limitações e ações reais. Conclusão parcial permanece parcial.
5. Separar contagens ETW, preservar dados parciais e integrar rede/jogos sem testes de carga automáticos.
6. Validar fluxos e publicação no Windows; documentar separadamente o que ainda depende do PC alvo.

## Implementação retomada em 16/10/2026

As correções foram continuadas a partir dos arquivos salvos; a base funcional e o mecanismo de mudanças reversíveis foram preservados.

| Fase | Arquivos/funções principais | Resultado e verificação |
|---|---|---|
| 1 — fontes | `WindowsTelemetryCollector`, `PassiveTelemetrySource`, `WindowsCounterSource`, `GpuEngineSource`, `ProcessTelemetryState`, `TelemetryHealthService`; `InitializeCoreAsync`/`RefreshInventoryAsync` | CPU/RAM/processos independem do inventário WMI. Provedores opcionais têm uma chamada por fonte, recuperação limitada e descarte de geração antiga; encerramento nativo não bloqueia UI. Campos acessíveis de processos são preservados. Testes de falha, reset, leitura lenta, encerramento lento, GPU e frescor. |
| 2 — painel | `MainWindow.xaml`, `MainViewModel.Analysis.cs` | Menu simples de seis destinos, botão principal, último diagnóstico, próximos passos e histórico. Modo avançado conserva ferramentas. Estado de coleta deriva de dados/horários, sem pontuação de saúde. |
| 3 — análise | `DiagnosticOrchestrator.RunAsync`, `ApplicationBackend.Analysis`, `StartAnalysisAsync` | Observação configurável, rede inicial/final, capturas opcionais confirmadas, cancelamento e relatório único persistido. Fontes que falham não eliminam dados das demais; leitura que excede cinco segundos não cria fila de chamadas. Testes do coordenador, persistência parcial e cancelamento. |
| 4 — interpretação | `DiagnosticAnalysisEngine`, `DiagnosticReport`, `TestReportExporter`, `ReportPrivacy` | Avaliação por componente com cobertura, evidência, confiança, limitação e ação. Fonte ausente não vira zero/Normal. Relatórios e processos exportados passam por anonimização; testes de escape HTML e preservação de metadados. |
| 5 — latência | `EtwTraceService`, `LatencyAggregator`, `LatencyInterpretation` | DPC/ISR/auxiliares/rejeitados/analisados separados, limites/perdas explícitos e leitura parcial preservada. Timestamp inválido não destrói duração válida. Compatibilidade com JSON antigo e regras de recorrência cobertas. |
| 6 — rede/jogos | `NetworkDiagnosticAnalyzer`, `MainViewModel.Network`, `PresentMonCaptureService`, `PresentMonPartialCapture`, `PresentMonSignatureVerifier` | Conexão selecionada de forma estável, contadores comparados por intervalo, driver/propriedades lidos automaticamente. PresentMon configurável com assinatura/CLI verificadas, captura por PID ou espera limitada; CSV parcial preservado. |
| 7 — otimizações/comparação | mecanismo existente preservado; `MainViewModel.Diagnostics` | Prévia, backup, confirmação e restauração existentes mantidos. Resultados importados não alteram telemetria atual; erro operacional limpa relatório/exportação anteriores. Comparações continuam exigindo condições e método compatíveis. |
| 8 — validação | `DiagnosticOrchestratorTests`, demais regressões, `PCAnalyzer.WpfHarness`, `PCAnalyzer.Verify`, workflows | Reconstrução completa com `-warnaserror`: zero erros/avisos. 242 testes C# passaram no Linux; 5 testes Python de pacote e 5 cenários PowerShell de publicação passaram. Validação WPF e leituras reais Windows serão registradas após a execução do workflow da versão. |

## Limites que permanecem explícitos

- O runner Windows pode validar CPU/RAM/processo próprio, persistência, navegação e renderização; não representa o Windows 10 Pro 22H2 ou o hardware do usuário.
- GPU NVIDIA/NVML, fallback GPU Engine, captura ETW com UAC, PresentMon durante jogo, overhead, DPI do computador alvo e aplicação/restauração no driver real exigem validação local.
- Temperatura CPU, SPD/timings/canais e SMART completo não são inferidos a partir de utilização. Benchmark GPU e novos testes de carga multithread/NVMe ainda precisam de implementação verificável; os testes controlados existentes mantêm seus limites.
- Relatórios parciais são esperados quando fontes ou capturas opcionais faltam. Identificar um módulo em uma captura não comprova que causou uma travada.
