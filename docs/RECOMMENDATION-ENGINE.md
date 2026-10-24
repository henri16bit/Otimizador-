# Motor de recomendações e interpretação — v0.7.0-preview.1

Esta evolução mantém a auditoria sem aplicação de ajustes e dá significado às leituras antes de sugerir uma investigação. A presença de uma propriedade, de um valor diferente ou de uma hipótese antiga de comparação não basta para criar uma melhoria.

Este documento descreve a implementação e a cobertura dos testes. Não declara a release publicada nem a validação Windows concluída. Os resultados das execuções ficam em [VALIDATION.md](VALIDATION.md).

## Como a recomendação é construída

[AuditRecommendationClassifier.cs](../src/PCAnalyzer.Core/AuditRecommendationClassifier.cs) centraliza sete classes funcionais: ajustável, informativa, referência documentada, dependente de teste, sem efeito documentado, não suportada e incompleta. As políticas específicas ficam nos arquivos parciais [Windows](../src/PCAnalyzer.Core/AuditRecommendationClassifier.Windows.cs), [HardwareNetwork](../src/PCAnalyzer.Core/AuditRecommendationClassifier.HardwareNetwork.cs) e [Nvidia](../src/PCAnalyzer.Core/AuditRecommendationClassifier.Nvidia.cs).

Cada resultado contém recomendação, motivo curto, justificativa, impacto possível, validação, riscos e, quando aplicável, um teste. As quatro confianças são independentes: leitura, interpretação, recomendação e benefício. A interface destaca a confiança da recomendação; as demais permanecem nas evidências técnicas. Benefício não medido não recebe confiança inventada.

Leitura indisponível não reutiliza um valor antigo para sugerir mudança. Propriedades desconhecidas recebem **Sem recomendação fundamentada**. Campos informativos, dispositivos virtuais ou desabilitados e campos MMCSS documentados como ignorados não viram prioridades automaticamente. **Manter** indica que a regra não sustenta alteração no escopo disponível; não certifica todo o computador.

[PerformanceAuditEngine.cs](../src/PCAnalyzer.Core/PerformanceAuditEngine.cs) aplica a política às avaliações, inclusive às antigas hipóteses de comparação. [AuditSettingPresentation.cs](../src/PCAnalyzer.Core/AuditSettingPresentation.cs) prepara valores legíveis, status e motivos, preservando evidências completas fora da lista principal.

Exemplos cobertos:

- `*IfType = 6` identifica Ethernet; não é um ajuste de desempenho.
- Win32PrioritySeparation exibe o valor observado, como `0x2A (42)`, sem substituição universal por `0x26`.
- A explicação específica de 2933 MT/s exige identificação de i7-10700K e placa B460. Outro processador ou placa, leitura ambígua ou taxa ausente não herda essa referência.
- Moderação de interrupções depende das alternativas anunciadas pelo driver e da relação entre processamento de pacotes e trabalho de CPU. Não é desativada nem declarada prejudicial pelo snapshot.
- NVIDIA distingue perfil global, perfil associado à aplicação, origem/herança e API gráfica não identificada. Diferença entre perfis não demonstra que potência máxima melhora o jogo.
- Prioridade e afinidade de processos mantêm executável, PID e informações disponíveis de criação e caminho. O processo do analisador ou a janela em primeiro plano não é apresentado como o jogo selecionado.

Os fatos de plataforma são obtidos em [StorageMemoryAuditProvider.cs](../src/PCAnalyzer.Hardware/StorageMemoryAuditProvider.cs). A identidade de processos vem de [WindowsConfigurationAuditProvider.cs](../src/PCAnalyzer.Hardware/WindowsConfigurationAuditProvider.cs). Informações ausentes ou ambíguas continuam ausentes ou ambíguas.

## DPC/ISR: conclusão limitada à evidência

[LatencyInterpretation.cs](../src/PCAnalyzer.Diagnostics/LatencyInterpretation.cs) usa o resultado de captura para produzir classificação, resumo, próximo passo e limites. [LatencyResultPresentation.cs](../src/PCAnalyzer.Diagnostics/LatencyResultPresentation.cs) mantém máximo, média, p95 e p99 de DPC e ISR separados, com as populações e contagens disponíveis.

A análise verifica duração, contexto, eventos válidos, perdas, rejeições, truncamento, posições temporais e consistência entre timeline e agregados por CPU/tipo. Contagens contraditórias, média maior que máximo e eventos fora da janela não podem sustentar uma conclusão de ausência de anomalia.

As saídas são **Sem anomalia relevante demonstrada na janela**, **Padrão que merece investigação**, **Inconclusivo** e **Captura inválida ou insuficiente**. O contrato também contém **Anomalia identificada por critério fundamentado**, mas essa saída permanece reservada: somente máximos e percentis não fornecem a referência aplicável ou evidência independente necessária.

Os critérios atuais de triagem são explícitos:

- Recorrência exige ao menos três eventos do mesmo módulo/tipo com duração de pelo menos 1.000 µs, em ao menos duas janelas de um segundo. Esse marcador gera investigação; não é um limite universal de defeito.
- Uma conclusão limitada de ausência do padrão exige contexto, ao menos 30 segundos, 100 eventos por tipo para p99, eventos espalhados por metade da janela e pelo menos dez janelas de um segundo, sem perdas ou inconsistências. Essas condições não garantem que o sintoma foi reproduzido nem que o jogo esteja sem problemas.
- Capturas curtas, sem contexto ou com cobertura insuficiente permanecem limitadas ou inconclusivas. Aumentar para 60 ou 120 segundos amplia a oportunidade de observação, sem garantir representatividade.

O próximo passo orienta repetir durante o sintoma e comparar com frametimes sincronizados quando aplicável. Não há correlação automática comprovada com carga de CPU, frametimes ou entrada física. DPC, ISR e input lag são grandezas diferentes.

[LatencyDeviceCorrelation.cs](../src/PCAnalyzer.Diagnostics/LatencyDeviceCorrelation.cs) relaciona os módulos da captura aos fatos disponíveis do inventário. O caminho real do binário do serviço tem origem registrada; um nome de serviço não é convertido por suposição em um arquivo `.sys`. Caminho exato, nome candidato, binário compartilhado e identidade ambígua têm tratamentos distintos. A relação inclui data do inventário, políticas MSI/afinidade e distribuição observada por CPU, sem afirmar aplicação efetiva durante a captura, conflito entre CPUs ou culpa do driver.

## Fases e interface

[DiagnosticOrchestrator.cs](../src/PCAnalyzer.Core/DiagnosticOrchestrator.cs) separa inventário, preparação de contadores e observação temporizada. [PerformanceAuditCoordinator.cs](../src/PCAnalyzer.Core/PerformanceAuditCoordinator.cs) informa progresso pelas fontes e leituras concluídas. O inventário termina pelas leituras e limites de cada fonte; o relógio da observação passiva começa depois. Falha ou cancelamento não é descrito como inventário concluído com sucesso.

O Início continua com **ANALISAR MEU PC** como ação passiva. **INVESTIGAR DESEMPENHO** navega para os testes manuais. O seletor DPC/ISR aceita 30, 60 e 120 segundos e transmite duração e contexto à confirmação e ao backend. Escolher uma categoria ou medição não inicia teste.

[MainViewModel.Consultation.cs](../src/PCAnalyzer.App/ViewModels/MainViewModel.Consultation.cs) reúne cobertura, configurações adequadas, até cinco prioridades no total e últimas medições válidas de DPC, ISR, RTT, variação do RTT, frametime e acesso à memória. Data, contexto e interpretação acompanham a leitura. Falhas posteriores não substituem uma medição válida; o histórico pode recuperar um resultado válido anterior a uma tentativa incompleta.

[MainWindow.xaml](../src/PCAnalyzer.App/MainWindow.xaml) apresenta as recomendações e a interpretação na categoria do teste. Abrir ou exportar usa o resultado dessa categoria. [AuditSettingRow.xaml](../src/PCAnalyzer.App/Controls/AuditSettingRow.xaml) mostra o motivo curto. [AuditSettingDialog.xaml](../src/PCAnalyzer.App/Controls/AuditSettingDialog.xaml) e seu [código](../src/PCAnalyzer.App/Controls/AuditSettingDialog.xaml.cs) mantêm uma rolagem principal, ações no rodapé, seção técnica recolhida e páginas de até 20 evidências copiáveis. Paginação, teclado e limites da área útil são verificados no harness WPF.

Os campos `Interpretation`, `NextStep` e `InterpretationStatus` de [TechnicalResult](../src/PCAnalyzer.Core/TestExecution.cs) sobrevivem à persistência e aparecem no [exportador](../src/PCAnalyzer.Reports/TestReportExporter.cs). Anonimização e escape HTML abrangem esses campos. Históricos anteriores, sem eles, continuam legíveis. A comparação mantém as verificações de identidade, contexto e método já existentes; valores incompatíveis não se tornam uma comparação causal.

## Mapeamento dos 20 requisitos de regressão

A tabela aponta testes existentes na árvore desta versão. A execução WPF requer Windows; a presença do cenário não equivale a aprovação da execução. Os nomes de métodos abaixo permitem localizar as verificações sem depender de contagens totais que mudam durante a integração.

| Nº | Requisito | Cobertura automatizada |
| --- | --- | --- |
| 1 | `*IfType` não aparece como otimização. | [RecommendationClassifierTests](../tests/PCAnalyzer.Tests/RecommendationClassifierTests.cs): `EthernetTypeIsDecodedAndRemainsInformationalDespiteGenericProviderFlags`. |
| 2 | Valores detectados aparecem na lista. | Mesmo arquivo: `SchedulerDisplaysTheObservedNumberAndJustifiesKeepingWithoutAUniversalReplacement`; [AuditPresentationTests](../tests/PCAnalyzer.Tests/AuditPresentationTests.cs): memória, processos, afinidade e perfis NVIDIA. |
| 3 | Informação não recebe comparação automática. | `PropertyPresenceAndProviderComparisonTextAreNotARecommendation` e `AProviderComparisonFlagDoesNotCreateAnOpportunityWithoutASpecificPolicy`, nos dois arquivos anteriores. |
| 4 | Win32PrioritySeparation não tem recomendação universal. | `SchedulerDisplaysTheObservedNumberAndJustifiesKeepingWithoutAUniversalReplacement` e `ObservedValuesDoNotBecomeUniversalRecommendations`. |
| 5 | 2933 MT/s não é defeito na plataforma identificada. | `MemoryPlatformExplanationRequiresTheActuallyIdentifiedCpuAndBoard`; [StorageAuditTests](../tests/PCAnalyzer.Tests/StorageAuditTests.cs) verifica identidade real, ausência e ambiguidade da plataforma. |
| 6 | DPC/ISR produz interpretação contextual. | [LatencyConsultationTests](../tests/PCAnalyzer.Tests/LatencyConsultationTests.cs): `CompleteDistributedCaptureProducesOnlyAScopedConclusion`, `LowMaximumWithoutContextOrTemporalSpreadIsNotEnough` e recorrência. |
| 7 | Captura curta não garante baixo input lag. | `ShortCaptureNeverCertifiesLowInputLag`, no arquivo anterior; qualidade incompleta e contagens contraditórias também são cobertas. |
| 8 | Recomendações têm motivos específicos. | `SpecificReasonsAndConfidenceDescribeTheRecommendationInsteadOfInventingMeasuredBenefit`; [harness WPF](../tests/PCAnalyzer.WpfHarness/Program.cs): motivos curtos nas linhas. |
| 9 | A lista principal não mostra evidência bruta. | `TechnicalEvidenceIsPreservedButExcludedFromSimpleFields`; harness: `compact rows hide technical identities`. |
| 10 | Informações técnicas continuam acessíveis. | Mesmo teste de apresentação; harness: expansão dos detalhes e paginação de 45 evidências sem perda de entradas. |
| 11 | Falha de coleta não vira otimização. | `FailedReadCannotPromoteAStaleValueOrItsComparisonHypothesis`, `UnavailableReadingDoesNotExposeStaleValuesOrSuggestAChange` e [PerformanceAuditIntegrationTests](../tests/PCAnalyzer.Tests/PerformanceAuditIntegrationTests.cs). |
| 12 | Modal tem rolagem e controles acessíveis. | Harness: uma rolagem principal, rodapé fora dela, páginas de até 20 evidências, foco no texto copiável, Escape e simulação de escala. Validação em monitores físicos permanece necessária. |
| 13 | Testes exigem ação do usuário. | Harness: navegação sem execução, análise padrão passiva, recusa da confirmação DPC; [DiagnosticPhasesTests](../tests/PCAnalyzer.Tests/DiagnosticPhasesTests.cs): somente captura selecionada recebe opções. |
| 14 | Resultado aparece na categoria do teste. | Harness: resultado manual de rede preserva auditoria; interpretação DPC e próximo passo; abrir/exportar selecionam o resultado da própria categoria. |
| 15 | Dados anteriores podem ser comparados. | [PerformanceAuditIntegrationTests](../tests/PCAnalyzer.Tests/PerformanceAuditIntegrationTests.cs): mudanças observadas, falhas de leitura, máquina incompatível, troca de hardware e reutilização de PID; [TestExecutionTests](../tests/PCAnalyzer.Tests/TestExecutionTests.cs): histórico legado e novos campos preservados. |
| 16 | Auditoria não modifica configurações. | [AuditOnlyBoundaryTests](../tests/PCAnalyzer.Tests/AuditOnlyBoundaryTests.cs), [OptimizationNetworkTests](../tests/PCAnalyzer.Tests/OptimizationNetworkTests.cs) e harness sem controles de aplicação. Os bloqueios da API e do auxiliar foram mantidos. |
| 17 | NVIDIA considera global e aplicação. | [NvidiaAuditTests](../tests/PCAnalyzer.Tests/NvidiaAuditTests.cs): associação por caminho, herança, candidato por nome e perfil do processo; classificador: diferença de perfil sem ganho presumido e API inaplicável. |
| 18 | Processo corresponde ao executável correto. | [WindowsAuditTests](../tests/PCAnalyzer.Tests/WindowsAuditTests.cs): `ProcessIdentityKeepsAnalyzerAndForegroundSeparateFromTheSelectedGame` e identidade indisponível; apresentação mantém cada processo separado. |
| 19 | Presença da propriedade não basta para classificar. | `PropertyPresenceAndProviderComparisonTextAreNotARecommendation` e cenário WPF `unknown properties never become optimization opportunities`. |
| 20 | **Manter** é usado com justificativa. | Testes de Win32, memória e `ModerationNeedsDriverAlternativesAndStillDoesNotPromiseAnImprovement`; `FilterFlagsSeparateAdequateComparisonConcernAndUnassessedWithoutClaimingGain`. |

As regressões adicionais cobrem correlação de drivers ambígua ou contraditória, progresso concorrente sem regressão, cancelamento entre fases, persistência dos campos interpretativos e anonimização das exportações.

## Limites e validação pendente no computador alvo

A verificação passiva no runner Windows coleta exemplos reais apenas do hardware e das APIs daquele ambiente. Não substitui testes no Windows 10 Pro 22H2 com os dispositivos do usuário. NVIDIA, Intel I219-V, NVMe, ETW/PresentMon durante o jogo, Vanguard e DPI físico de 125%/150% precisam dessa validação específica.

Não foram medidos ganhos de FPS ou latência. O programa não aplica configurações, não mede input lag físico, não comprova causalidade de um driver por coincidência de eventos e não infere XMP, timings ou canais a partir de uma taxa SMBIOS. A classificação `GroundedAnomaly` permanece reservada. Capturas de cargas diferentes, identidades ambíguas e dados incompletos devem conservar seus limites ao comparar resultados.
