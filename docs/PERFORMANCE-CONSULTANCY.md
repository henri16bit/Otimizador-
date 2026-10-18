# Auditoria e consultoria de performance

Esta entrega evolui a implementação existente a partir de `1facb31` (v0.4.0-preview.3). O escopo vigente é **somente leitura**: nenhum ajuste operacional é aplicado ou restaurado, inclusive por APIs legadas ou pelo auxiliar elevado. Registros antigos continuam legíveis. ETW manual continua exigindo confirmação e UAC; carga de CPU/memória, leitura de arquivo, ICMP e PresentMon permanecem opcionais.

## Revisão do ponto de partida

Foram preservados coleta passiva CPU/RAM/processos/NVML, cadência e recuperação, captura/importação ETW, PresentMon, análise coordenada, gráficos, SQLite, relatórios, tratamento de inicialização e publicação do pacote Windows. A versão anterior tinha caminhos funcionais de escrita de energia/rede/restauração e redirecionava a análise para Resultados. Esses comportamentos foram substituídos para atender ao novo pedido.

## Módulos entregues

| Área | Leitura/funcionalidade | Interpretação e limites |
| --- | --- | --- |
| Motor | Catálogo de regras com ID, categoria, método, pré-requisitos, compatibilidade, evidência, hipótese, suporte, validação e fontes. Dados brutos separados das avaliações. | Valor encontrado não prova reconhecimento, aplicação ao jogo ou benefício. Ausência, erro e recurso não suportado são estados distintos. |
| Windows | Versão/build em ambas as vistas do Registro; PrioritySeparation; MMCSS/tarefas; IFEO PerfOptions; Game Mode/HAGS/tela cheia/preferências gráficas; mouse/filas/curvas; memória virtual; energia AC/DC; processos/prioridades/afinidade; serviços, inicialização e tarefas; QPC e opções BCD quando legíveis. | Registros de contratos não públicos são identificados como não verificáveis. Perfis MMCSS não demonstram adesão do jogo. Não se altera timer, BCD ou energia. [Fontes e campos](WINDOWS-AUDIT-SOURCES.md). |
| NVIDIA | NVAPI DRS pública: base/global, associação por executável/processo, perfil VALORANT quando encontrado, valores públicos, origem herdada/local/predefinida e diferenças globais. | Não lê opções privadas por IDs adivinhados. Low Latency Mode privado e Reflex interno ao jogo não são declarados ativos. [Detalhes](NVIDIA-AUDIT.md). |
| MSI/IRQ/afinidade | Dispositivos PnP presentes, driver, recursos IRQ atribuídos, capacidade PCI, MSISupported/MessageNumberLimit, DevicePolicy/AssignmentSetOverride, grupos/SMT/topologia e relações USB/HID. | Capacidade, configuração e recursos são evidências separadas. Registro não prova MSI efetivo; compartilhar CPU não prova conflito. Polling real e input lag não medidos. [Detalhes](DEVICE-AUDIT.md). |
| Rede | Mapeamento GUID/índice/PnP verificado, propriedades realmente expostas, RSS, energia, estatísticas e perfis TCP. CLIXML é decodificado; saída técnica não vira conclusão de desempenho. | Não presume opções iguais em todos os drivers, nem que TCP controla tráfego UDP. Nenhum ICMP é enviado pela auditoria. [Detalhes](NETWORK-STORAGE-AUDIT.md). |
| RAM/discos | Módulos e velocidade SMBIOS; discos físicos/firmware/barramento/saúde; contadores de confiabilidade apenas com associação única; localização Windows/pagefile. | Sem invenção de SPD/XMP/timings/canais. SMART bruto e dados não reportados pelo Storage permanecem indisponíveis; nenhum benchmark é automático. |
| Teste adicional de memória | Leitura, escrita, cópia e acesso dependente em buffers de 1/16/64 MiB; até 128 MiB de buffers, exige 512 MiB disponíveis, etapas interrompidas não são consideradas concluídas. | Mede payload e ns/acesso no processo, com cache/TLB/runtime/agendamento. Não isola DRAM nem comprova integridade da RAM. Não escreve arquivos nem altera afinidade. |
| Interface/histórico | Recomendações substitui Otimizações. Relatório na própria tela abaixo da ação, cobertura por área, fontes e detalhes recolhidos, leitura do diário legado. Snapshots SQLite e comparação de valores comuns com mesma identidade de inventário. | Sem nota fictícia, recomendação universal ou alteração dentro do aplicativo. Dados de teste só aparecem na suíte. |
| Exportação | Auditoria estruturada em JSON e relatório HTML A–K, com evidências, riscos, confiança, validação e fontes. Anonimização também abrange identificadores novos da auditoria. | Snapshot local completo não é anonimizado; a cópia exportada é. Comparação é descritiva e não atribui ganho a uma mudança externa. |

Os provedores têm limite de consulta e execução isolada. Uma chamada nativa que não terminou não é enfileirada novamente. A auditoria da análise geral ocorre após a janela de telemetria para evitar interpretar seu trabalho de inventário como carga do usuário. Valores de configuração são pontuais e não representam um snapshot atômico do Windows inteiro.

## Validação e próxima etapa

Resultados executados e a versão publicada constam de [VALIDATION.md](VALIDATION.md). Os testes cobrem bloqueios de escrita, semântica, tipos, identidade, máscaras/ABI, parsing, persistência, privacidade, comparação, cancelamento e interface. O verificador Windows acrescenta leituras reais de Registro, processo próprio, topologia e snapshots; um runner virtual não valida a RTX 3060, Intel I219-V, dois NVMe nem periféricos do usuário.

Ainda exigem validação no computador alvo: leitura DRS no driver NVIDIA instalado; propriedades e identidade I219-V; correspondência de controladores/IRQ/grupos com ferramentas de referência; associações e sensores dos dois NVMe; ETW e PresentMon durante carga permitida; aparência com DPI real e custo de CPU/RAM do aplicativo. Nenhuma etapa de aceitação pede aplicar ou restaurar ajustes pelo PC Analyzer.

Recursos ainda não implementados ou não verificáveis pelos métodos atuais: distribuição efetiva de todas as interrupções correlacionada à afinidade configurada; adesão MMCSS por thread; SPD/timings/XMP/canais e latência DRAM isolada; resolução de timer rastreada por processo/fonte efetiva do QPC; polling HID e clique-para-fóton; opções NVIDIA privadas/Reflex; latência de rede sob carga controlada; mapeamento automático completo da instalação do jogo aos discos; benchmark gráfico/CPU multithread e correlação causal de configurações com ganhos. São pendências explícitas, sem telas simulando implementação.

O próximo passo de produto é validar as leituras no PC alvo, registrar a carga e repetir sessões equivalentes. Só depois há evidência para ampliar regras de interpretação ou recomendar comparações específicas.
