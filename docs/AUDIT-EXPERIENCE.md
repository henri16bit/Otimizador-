# Experiência de auditoria por categoria

Esta atualização reorganiza a interface do projeto existente. Os coletores continuam produzindo observações, o motor interpreta as evidências e uma camada de apresentação prepara nomes, valores legíveis, status e próximos passos. O catálogo completo permanece disponível; simplificar a tela não descarta a evidência do snapshot.

## Fluxo principal

Em **Início**, **ANALISAR MEU PC** executa a observação padrão e a auditoria de configurações. O resumo aparece na mesma tela, com até cinco pontos priorizados e acesso às categorias. A configuração padrão não inicia ping, benchmark, PresentMon ou ETW. Esses recursos são escolhidos manualmente e passam por confirmação antes de começar.

O menu principal possui dez destinos:

| Destino | O que apresenta |
| --- | --- |
| Início | Resumo do computador, análise e principais pontos encontrados. |
| Windows | Agendamento, MMCSS, IFEO, energia, serviços, processos, gráficos e temporizadores. |
| NVIDIA | Leituras NVAPI DRS, perfil base/global, perfis de aplicativos e origem herdada quando disponível. |
| Rede | Estado da conexão, configurações anunciadas pelo driver e teste manual de RTT/jitter. |
| MSI / IRQ / USB | Dispositivos agrupados, configuração de interrupções e afinidade interpretada quando possível. |
| Latência | Medições separadas de DPC, ISR, frametime, RTT e acesso à memória. |
| Hardware | CPU, módulos de RAM e unidades identificadas, com testes controlados opcionais. |
| Jogos | Leituras relacionadas ao jogo detectado e captura opcional de frames. |
| Recomendações | Configurações que merecem investigação ou comparação, conforme a evidência disponível. |
| Avançado | Inventário, processos, relatórios completos, fontes, histórico, importações e preferências. |

Nas categorias de configuração, cada linha mostra nome, descrição curta, **Atual**, **Recomendação**, **Status** e **Detalhes**. A busca usa o nome; os filtros são **Todas**, **Melhorias possíveis**, **Vale testar**, **Adequadas** e **Não avaliadas**. Também há seleção de grupo e ordenação por prioridade ou nome. A busca e os filtros de cada categoria são preservados durante a navegação desta execução do aplicativo.

A lista usa virtualização com reciclagem e uma área de rolagem limitada. Evidências equivalentes de Registro 32/64 bits e inventários repetitivos podem ser agrupados; valores distintos continuam separados. Todas as observações originais permanecem associadas à linha.

## Explicação e recomendações

**Detalhes** abre uma janela vinculada à janela principal, com valor atual, recomendação, justificativa, impacto possível, confiança, riscos e método de validação. O foco inicial fica em **Fechar**; `Tab` permite percorrer as ações e `Esc` fecha. **Detalhes técnicos** começa recolhido e conserva identidade, caminho do Registro, valor original, origem, documentação e limitações.

**Testar comportamento** aparece quando existe um teste compatível. Ele abre a categoria da medição e usa o comando já existente, incluindo confirmação e escolha de processo/arquivo quando necessária. O botão não aplica uma configuração antes de medir. Sem teste automatizado apropriado, **Como validar** orienta a investigação.

| Orientação | Significado |
| --- | --- |
| Manter | A leitura apresentada não sustenta uma mudança. Não certifica desempenho ideal do computador. |
| Vale comparar | Existe uma hipótese para comparação; não há benefício demonstrado. |
| Depende de medição | O valor não permite determinar uma alternativa melhor para a carga. |
| Investigar antes de alterar | Há evidência que merece análise adicional; a correção não é inferida automaticamente. |
| Não determinado | A leitura necessária está ausente, indisponível ou falhou. |

O modelo comporta uma recomendação específica fundamentada, mas as regras atuais não emitem um valor substituto universal. Não se recomenda um número só porque difere de um tutorial. Presença no Registro, configuração reconhecida, estado efetivo e benefício medido continuam sendo conclusões distintas.

Código PnP 22 indica dispositivo desabilitado. Dispositivos virtuais e adaptadores de depuração, por si só, não demonstram perda de desempenho no jogo. Esses casos ficam informativos. GPU Priority/SFIO Priority documentados como ignorados não preenchem a lista de melhorias. Se nenhuma melhoria tiver evidência suficiente, a interface informa isso.

## Testes e resultados

Rede, hardware, jogos e latência mostram o resultado na própria seção. Progresso, cancelamento e estado parcial continuam disponíveis; o resultado é guardado no histórico. Gráficos e informações extensas ficam recolhidos. Os indicadores da categoria são apresentados a partir da execução correspondente, sem substituir evidências por valores de outro teste.

O painel de latência mantém as unidades e o alcance de cada medição. DPC/ISR usam durações reais, média ponderada por evento e percentis dos eventos retidos quando há amostra suficiente. Agregados por CPU e por módulo não são somados duas vezes. Perdas e truncamento limitam a conclusão. Vários streams de frames ou tamanhos de buffer não são resumidos como se fossem um único cenário.

RTT de ICMP não mede a latência do tráfego do jogo. Acesso à memória inclui CPU, caches, TLB e runtime, sem isolar DRAM. DPC não é input lag físico. Atrasos de agendamento sem captura específica permanecem **Não medido**. Não há escore universal de latência.

Em Rede, falhas distinguem permissão negada, comando ausente, suporte indisponível e erro de leitura. Campos vazios não viram sucesso. A identificação continua baseada em GUID, com fallback restrito a associações não contraditórias. Propriedades TCP são apresentadas como TCP; não descrevem UDP. Só aparecem opções efetivamente anunciadas pelo driver.

## Componentes e funcionalidades preservadas

| Arquivo/componente | Responsabilidade |
| --- | --- |
| `src/PCAnalyzer.Core/AuditSettingPresentation.cs` | Agrupamento, conversão de valores, status, prioridade e orientação de apresentação, sem consultar ou escrever no sistema. |
| `src/PCAnalyzer.App/Controls/AuditSettingRow.xaml` | Linha reutilizável de configuração com valores e ação de detalhes. |
| `src/PCAnalyzer.App/Controls/AuditSettingsList.xaml` | Busca, grupos, filtros, ordenação e lista virtualizada. |
| `src/PCAnalyzer.App/Controls/AuditSettingDialog.xaml` | Explicação, evidência recolhida, navegação por teclado e encaminhamento para teste. |
| `src/PCAnalyzer.App/ViewModels/MainViewModel.Experience.cs` | Categorias, filtros preservados e resultados vinculados às seções. |
| `src/PCAnalyzer.App/MainWindow.xaml` e `App.xaml` | Navegação, tema escuro, hierarquia tipográfica e modelos compactos de resultado. |
| `src/PCAnalyzer.Diagnostics/LatencyResultPresentation.cs` | Indicadores DPC/ISR e limites da amostra, sem estimar input lag. |
| `src/PCAnalyzer.Network/NetworkInspector.cs` e `NetworkConfigurationAuditProvider.cs` | Estados de leitura e associação entre conexão, propriedades e evidências. |
| `src/PCAnalyzer.Core/PerformanceAuditEngine.cs` | Priorização informativa de dispositivos desabilitados e casos sem efeito documentado. |

Foram preservados descoberta e coleta passiva, auditorias Windows/NVIDIA/dispositivos/rede/armazenamento, testes manuais, histórico SQLite, exportação, anonimização, comparação, importação ETL/CSV, coleta PresentMon e auxiliar ETW. As ferramentas detalhadas continuam acessíveis por **Avançado**, sem duplicar o menu principal.

As proteções de somente leitura permanecem na interface, backend e auxiliar. Não há aplicação/restauração de Registro, perfis NVIDIA, energia, prioridades, MSI, afinidade ou propriedades de rede. Medições manuais podem gerar carga ou tráfego confirmado; não alteram esses ajustes.

## Validação e limites

As regressões de apresentação cobrem agrupamento sem perda de evidência, ausência de valores fictícios, remoção de detalhes técnicos do resumo e prioridade de recomendações. As regressões de rede cobrem identificação do adaptador e estados de falha. A apresentação de latência possui testes de agregação, percentis, recorrência e ausência de amostra. A suíte WPF exercita navegação, filtros, modais, teclado, rolagem, virtualização e resultados por categoria, além dos fluxos anteriores de histórico e cancelamento.

A entrega **v0.6.0-preview.5** foi publicada após aprovação de 404 testes C#, 41 cenários WPF, verificações de rede/empacotamento/publicação, leitura real no runner e abertura do executável. As capturas finais foram inspecionadas. Resultados, download e limites estão em [VALIDATION.md](VALIDATION.md). As verificações visuais exigem tamanho de fonte e contraste legíveis no botão principal, contraste nos textos da explicação, valores das linhas efetivamente renderizados após a atualização dos bindings e captura completa sem corte por deslocamento de layout.

O roteiro visual inclui 1366 × 768 e 1920 × 1080, além de dimensões lógicas equivalentes a 125% e 150%. Renderização automatizada nessas dimensões não comprova mudança real de DPI entre monitores, legibilidade percebida, teclado de acessibilidade ou escala do Windows na máquina do usuário. A aceitação em monitores reais continua necessária.

Também permanecem pendentes a validação no Windows 10 Pro 22H2 alvo, Intel I219-V, RTX 3060/NVAPI, ambos os NVMe e capturas durante carga representativa. Low Latency Mode privado, Reflex interno do jogo, estado efetivo de MSI, polling USB real, SPD/timings/canais, sensores não expostos e input lag físico não são inferidos. A RAM a 2933 MT/s na plataforma B460 não é automaticamente classificada como defeito. Consulte [PERFORMANCE-CONSULTANCY.md](PERFORMANCE-CONSULTANCY.md) para o escopo das leituras e pendências técnicas.
