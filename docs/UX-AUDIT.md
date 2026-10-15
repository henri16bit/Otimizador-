# Auditoria da experiência e das integrações — 14/10/2026

Base auditada: `d1850b2` (`v0.2.0-preview.3`). Inspeção antes das alterações: solução de 12 projetos, comandos WPF/MVVM, backend, coleta Windows/NVML, ETW, PresentMon, rede, testes controlados, SQLite, relatórios, motor de regras e alterações reversíveis. Nenhum botão de benchmark usa números simulados. Fixtures de testes não são medições de produção.

Os estados abaixo qualificam a base anterior, não promessas sobre a refatoração. Testes portáveis e smoke WPF anterior não substituem execução no computador do usuário.

| Recurso | Estado na base | Evidência e correção necessária |
|---|---|---|
| Build, estatísticas, parsing, protocolos e regressões | IMPLEMENTADO | 116 testes automatizados anteriores; baseline repetido nesta revisão. |
| Navegação e layout WPF | IMPLEMENTADO / PARCIALMENTE IMPLEMENTADO | 8 páginas, atalhos e 7 abas internas verificados no Windows. Falta integrar atividade e histórico no dashboard. |
| CPU, GPU NVML, RAM, discos, rede e processos | PARCIALMENTE IMPLEMENTADO / NÃO VALIDADO | Coleta real, origem, unidade, timestamp e ausência explícita. Sensores/drivers específicos e overhead no PC real ainda não validados. |
| Metadados e ajuda das métricas | PARCIALMENTE IMPLEMENTADO | Nomes em `MetricPresentation`; fonte/método acessíveis. Falta explicação central com interpretação, fatores e limites. |
| Inventário e primeiro uso | PARCIALMENTE IMPLEMENTADO | Descoberta passiva e capacidades existem; dashboard resume apenas CPU. Falta orientação e identificação dos demais componentes. |
| Testes CPU/RAM/leitura/rede | IMPLEMENTADO / PARCIALMENTE IMPLEMENTADO | Ações reais, limites/cancelamento; não são benchmarks padronizados. Progresso de rede e ligação ao dashboard ausentes. |
| Estado e resultado dos testes | COM DEFEITO | `IsBusy` mistura exportação e teste; resultado fica em texto único; conclusão força navegação; falha pode deixar gráfico anterior visível. |
| Histórico de sessões de telemetria | IMPLEMENTADO | SQLite transacional, abertura/comparação/exportação existentes. |
| Histórico de testes | NÃO IMPLEMENTADO na interface | JSONs reais existem em `results`, mas não há índice, reabertura, comparação ou registro unificado de falhas/cancelamentos. |
| DPC/ISR | PARCIALMENTE IMPLEMENTADO / NÃO VALIDADO | ETW real, agregações, módulos, percentis, timeline e limites; UI apresenta texto, falta gráfico e interpretação integrada. Captura/UAC depende de Windows. |
| Frametime/jogos | PARCIALMENTE IMPLEMENTADO / NÃO VALIDADO | CSV real, estatísticas por swapchain, gráfico relativo e captura opcional. Não há correlação segura sem alinhamento de relógio. Vanguard não validado. |
| Regras e evidências | PARCIALMENTE IMPLEMENTADO | Triagem CPU/RAM com cobertura temporal; ausentes interpretações de ping, recorrência DPC e janelas de frametime. |
| Ajustes e reversão | IMPLEMENTADO / NÃO VALIDADO | Prévia, confirmação, UAC, journal durável, valor esperado e releitura; testes com operações isoladas. Aplicação/restauração real exige validação autorizada no PC. |
| Gráficos | PARCIALMENTE IMPLEMENTADO | Dados reais, lacunas e unidades; falta janela selecionável e estabilidade de escala. |
| Logs/ciclo de vida | IMPLEMENTADO / PARCIALMENTE IMPLEMENTADO | Logs limitados/sanitizados, buffers e cancelamento de captura; falta registro de execução recuperável após encerramento inesperado. |
| Temperatura CPU, SPD/timings, SMART/desgaste NVMe, benchmark GPU, latência física de entrada | NÃO IMPLEMENTADO | Não há provedor verificável; exibir indisponibilidade, nunca substituir por estimativa de saúde ou botão funcional fictício. |
| Windows/agendamento avançado | PARCIALMENTE IMPLEMENTADO | Inventário SO, filas/trocas/interrupções e processos. Não há captura de stacks/agendamento de threads para atribuir um gargalo. |

## Ordem de correção

1. Dashboard: diagnóstico selecionável com objetivo, impacto e permissões; ajuda reutilizável; inventário e primeiro uso; atividade/resultados sem navegação automática.
2. Estado central de execução: preparação, execução, terminal; progresso mensurável ou indeterminado; cancelamento; uma operação incompatível por vez; histórico durável e recuperação.
3. Resultado estruturado com métricas, séries, evidências, limitações e janela de telemetria; reabertura, exportação e comparação.
4. Interpretação técnica de rede/DPC/frames, integração contextual com recomendações e ajustes, discos separados, gráficos investigativos e fontes indisponíveis explícitas.
5. Testes de estados, persistência, falhas/cancelamento, relógios e UI no Windows; publicar somente depois da verificação.

## Implementação da revisão — 15/10/2026

| Fase | Entrega e arquivos principais | Verificação e limite |
|---|---|---|
| Dashboard e orientação | `MainWindow.xaml`, `MainViewModel.Diagnostics.cs`, `MetricHelp`, `MetricCatalog` e `TestCatalog`: objetivo antes do teste, atividade destacada, resumo do hardware, primeiro uso dispensável, ajuda curta com detalhes expansíveis, fontes e horários. | Compilação XAML e catálogo verificados. Dados ausentes continuam ausentes. |
| Execução e histórico | `TestExecution.cs`, `SQLiteHistoryStore.cs`: estados separados, progresso medido ou indeterminado, cancelamento, uma execução por vez, resultado completo no SQLite, migração v1→v2 e recuperação de interrupções. A conclusão preserva a página escolhida. | Testes cobrem transições, exceções, cancelamento parcial, eventos tardios, falha de persistência e reabertura. |
| Rede | `MainViewModel.Network.cs`, `NetworkInspector.cs`, `GatewaySelection.cs`, `ManualPingService.cs`: escolher conexão → ler detalhes → testar/entender propriedades. Driver, link, RSS, energia e estatísticas têm origem e indisponibilidade explícitas. Ping mostra progresso, perda, RTT, jitter e série temporal. | Testes cobrem múltiplos gateways, adaptador desconectado, lacunas e interpretação limitada a ICMP. Cmdlets/drivers específicos exigem validação no PC. |
| Evidências e gráficos | `LatencyInterpretation.cs`, `FrameInterpretation.cs`, `TemporalDiagnosticAnalyzer.cs` e controles de gráfico: recorrência DPC/ISR, picos de frametime, janela selecionável, escala estável e seleção individual de disco. | Correlação somente com relógio UTC conhecido e amostras suficientemente próximas; nunca afirma causalidade. CSV relativo não ganha timestamp inventado. |
| Resultados e comparação | `TestReportExporter.cs`, `TestResultComparer.cs`: indicadores, séries, achados, evidências, contexto e limites recuperáveis; HTML/JSON com anonimização; comparação exige mesmo teste finalizado e identidade de destino/método. | Testes de privacidade, HTML, persistência e recusa de comparação entre destinos diferentes. Repetições controladas continuam necessárias para avaliar ganhos. |
| Ajustes | `ReversibleChangeEngine.cs`, backend e tela de ajustes: valor original precisa coincidir com a prévia aprovada; propriedades sem valor original restaurável ficam somente leitura. Releitura após alteração de rede. | Teste de conflito com a prévia garante nenhuma escrita ou registro aplicado. Operações reais continuam manuais, com confirmação e UAC. |
| Interface executada | Projeto separado `PCAnalyzer.WpfHarness`, workflows e registro de release: cenários usam janela WPF real e backend de teste para progresso, navegação, conclusão, cancelamento, falha, reabertura, leitura de rede e ausência de sensores. | O executável de teste não entra no ZIP. A publicação exige esses cenários e o smoke das oito páginas, sete abas e tamanho compacto no Windows. Resultado remoto fica em Git notes e artefatos do workflow. |

A suíte portátil passou de 116 para 142 casos. A validação corrente e seus comandos estão em [VALIDATION.md](VALIDATION.md). As capturas do runner mostram renderização real; os valores de cenário são fixtures identificadas, não medições do computador do usuário.

## Trabalho que continua dependente de implementação ou hardware

- Temperatura/clock efetivo da CPU, SPD/timings/canais, SMART/desgaste NVMe, benchmark GPU, scheduling/stacks e latência física de entrada não ganharam provedores fictícios.
- Captura real ETW/PresentMon, políticas de jogos, precisão dos sensores, impacto em FPS e aplicação/restauração de propriedades do driver precisam do roteiro [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md) no computador alvo.
- A correlação temporal usa somente dados retidos e alinhados. Não resolve clocks QPC sem frequência conhecida, sincronização externa ou causalidade.
- O pacote é uma prévia portátil. Não há instalador/assinatura nem certificação de funcionamento no Windows 10 Pro 22H2 do usuário.
