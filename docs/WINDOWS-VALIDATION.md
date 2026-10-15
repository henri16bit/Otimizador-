# Validação no computador Windows do usuário — roteiro

Este documento é um roteiro, **não um registro de resultados aprovados no computador alvo**. O desenvolvimento e a suíte portátil usam Linux; os workflows executam a interface WPF no runner Windows, com resultados registrados em [VALIDATION.md](VALIDATION.md) e nas notas Git por commit. Isso não comprova WMI/Win32, NVML, captura ETW/PresentMon, alteração NetAdapter ou restauração no hardware do usuário.

Alvo solicitado: **Windows 10 Pro 22H2 x64**, i7-10700K (8 físicos/16 lógicos), ASUS TUF Gaming B460-PLUS, RTX 3060 12 GB, 16 GB DDR4 em dois módulos, dois SSDs NVMe, Intel I219-V e monitor 240 Hz. Os valores são referência de conferência informada pelo usuário, não dados a serem inseridos na aplicação. Preserve os modelos/valores efetivamente descobertos quando houver diferença.

A edição regular Windows 10 Pro 22H2 saiu do suporte regular em 14/10/2025 e não consta como edição regular suportada na [matriz .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Executar este roteiro verifica compatibilidade técnica na máquina escolhida, sem estender o suporte oficial do sistema/runtime. Uma validação adicional numa edição/build suportada é necessária para declarar suporte a esse ambiente.

## Registro de cada execução

Anote data/hora e fuso, commit/pacote e hash, build do Windows, BIOS, driver NVIDIA/Intel, modelos dos discos, resolução/refresh, aplicações ativas, plano de energia, carga reproduzida, intervalo/modo de coleta e ferramentas de referência. Anexe somente exportações com privacidade apropriada. Para cada item, registre `Aprovado`, `Falhou`, `Sem suporte` ou `Não executado`, evidência, limitação e passos para reproduzir. Não converta `Sem suporte` em uma leitura zero.

Os testes ativos e as mudanças abaixo são **manuais**. A suíte normal do repositório não executa alterações reais. Comece com observação passiva e mantenha acesso local para recuperação antes de validar uma propriedade de rede.

## 1. Publicação e inicialização

1. Extraia o pacote Windows x64 completo, incluindo `worker/`, para uma pasta local. Abra `PCAnalyzer.exe` como usuário normal.
2. Confirme que a abertura não inicia ping, CPU/RAM/disco de laboratório, PresentMon, kernel ETW ou alteração de plano/adaptador.
3. Confirme que descoberta demorada e coleta deixam a UI responsiva. Mude seção, selecione série, examine tooltip/detalhes, pause/retome e minimize.
4. Verifique `%LocalAppData%\PCAnalyzer`: histórico/log/preferências são locais e não contêm credenciais. Registre uma pasta de instalação sem permissão de escrita para confirmar que dados continuam na pasta do usuário.
5. Remova temporariamente a pasta `worker` de uma cópia de teste: ações privilegiadas devem apontar ausência do auxiliar; coleta passiva deve continuar. Recoloque a pasta antes dos testes seguintes.

Critérios cobertos: inicialização, fluxo passivo, ausência de carga automática, UI responsiva e indisponibilidade explícita. Registrar screenshot sem gráfico preenchido por dados fictícios.

### Fluxo integrado de diagnóstico

1. Na **Visão geral**, selecione **Análise do sistema**, leia objetivo/duração e inicie. Confira etapa, tempo e cancelamento; o perfil deve retomar a cadência anterior ao terminar.
2. Durante um ping manual, navegue para **Componentes**. A conclusão deve manter essa página e atualizar o resumo na **Visão geral**.
3. Cancele outro teste. O estado deve ser **Cancelado**, com dados parciais identificados quando existirem; gráficos do teste anterior não devem aparecer como novos.
4. Em **Histórico**, reabra o teste finalizado e confira indicadores, séries, fontes e horários. Feche e abra o programa para repetir a consulta.
5. Compare dois pings para destinos diferentes: o programa deve explicar por que não calcula diferenças. Para o mesmo destino, diferenças são descritivas e não demonstram ganho.
6. Exporte o teste em HTML/JSON com anonimização e confira dados, contexto e ausência de identificadores pessoais esperados. Desconecte uma fonte somente em ambiente de teste adequado e confira `—` e a explicação de indisponibilidade.
7. Somente num ambiente de teste, interrompa o processo durante um diagnóstico sem alteração de configuração. Ao reabrir, o registro deve indicar falha/interrupção com duração desconhecida, sem reiniciar automaticamente o teste.

## 2. Inventário e relações de hardware

Compare com Informações do Sistema, Gerenciador de Dispositivos e fontes oficiais:

| Item | Evidência esperada e cuidado |
| --- | --- |
| Windows/BIOS/CPU/placa | Build, fabricante/modelo, núcleos e lógicos reais; verificar campos ausentes do SMBIOS. |
| DIMMs | Capacidade de cada módulo, slot, fabricante/SKU quando exposto, `Speed` separado de `ConfiguredClockSpeed`. Não certificar SPD, timings/XMP ou canal efetivo por WMI. |
| Contexto DDR | 2933 MT/s na plataforma informada não deve produzir alerta automático de defeito ou recomendação de frequência incompatível. |
| GPU | Modelo e driver. VRAM confiável depende de NVML; ausência de NVML não pode exibir 4 GB por truncamento de `AdapterRAM`. |
| Discos | Dois dispositivos realmente enumerados, modelos e capacidade, volumes/partições corretamente associados, Windows/pagefile. `BusType=17` quando o provedor Storage reportar NVMe; `SCSI` não é prova de SATA. |
| Monitores | Principal, resolução e refresh do desktop comparados às Configurações de Vídeo. Não equiparar 240 Hz a FPS medido ou VRR. |
| Rede | InterfaceGuid, adaptador ativo, driver, link negociado, gateways e recursos existentes no driver I219-V. |

Registre limitações de firmware/provedor individualmente. Localização de jogo deve vir de seleção explícita quando necessária, sem varredura indiscriminada de pastas pessoais.

## 3. Telemetria e gráficos

Execute uma janela em repouso e outra durante uma carga conhecida iniciada manualmente. Use mesmas fontes/intervalos e compare médias de janelas equivalentes; não trate um valor instantâneo visual de outra ferramenta como referência sincronizada.

- CPU total/lógica/processos: compare com PerfMon/Gerenciador de Tarefas. Primeiro lote de uma taxa pode estar `Collecting`. Conferir processo encerrado/protegido e reutilização de PID sem misturar histórico. CPU de processo é normalizada pelo total de lógicos.
- RAM: compare total/disponível/commit/limite. Commit e working set são conceitos diferentes. Confirme unidades em bytes e estado nulo quando uma fonte falha.
- Discos: compare I/O/fila/latência de cada índice físico com PerfMon. Em repouso sem I/O, a latência deve ficar ausente/insuficiente, não ser apresentada como medição zero. Throughput do Test Lab com cache não é uma referência de desempenho NVMe bruto.
- NVML: compare VRAM/temperatura/clocks/utilização com `nvidia-smi` em janelas próximas, documentando a janela interna do driver. Clock/P-state/flag isolado não pode provar throttling. Teste comportamento sem NVIDIA/NVML em máquina de teste adequada.
- Rede: link negociado não é throughput; as taxas derivam de deltas. Nenhum RTT aparece antes de ping manual.
- Gráficos: gaps e estados indisponíveis devem permanecer identificáveis; conferir timestamp, método, intervalo efetivo e agregado. Alterar intervalo e minimizar; medir a redução de cadência.

Registre `collector.duration.ms`, `collector.cpu.percent` e `collector.working_set.bytes`. Compare o custo do próprio aplicativo em repouso e em cenário gráfico repetível. Não há limiar universal já aprovado. Verifique limite de buffer e nota de amostras descartadas em uma execução prolongada.

## 4. Triagem e contexto

Produza uma observação legítima suficientemente longa. A regra CPU exige ao menos 10 amostras e 8 s com utilização ≥90% em ≥80% das amostras recentes. Confira evidências e nível de confiança: carga sustentada é investigação possível, não prova de queda de FPS. A regra de RAM exige disponibilidade baixa com commit alto repetidos; não force exaustão perigosa de memória apenas para produzir um alerta.

Sem amostras, mostrar dados insuficientes. Sem evidência nas regras limitadas, a mensagem deve dizer que não há motivo demonstrado para modificar configurações, sem certificar o hardware ou desempenho esperado do modelo. Verificar que as conclusões preservam as limitações de CPU térmica/SPD/SMART ainda não implementados.

## 5. Ping manual e rede

Em **Investigar → Rede**, use **Detectar conexões**, escolha o adaptador e clique em **Ler detalhes**. Confira nome, link, gateway, driver, RSS, energia e propriedades com o Gerenciador de Dispositivos/PowerShell. Troque o adaptador durante uma consulta: resultados da seleção anterior não devem preencher a nova seleção. Propriedades sem enumeração restaurável devem ficar somente leitura, com motivo visível.

Selecione gateway ou destino controlado e acione o comando de ping. A interface envia 20 sondas com timeout conservador. Confira contagem, RTT min/média/max/p95 e jitter definido como média de diferenças absolutas entre respostas sucessivas; uma ausência não deve ligar pares separados por uma lacuna. Compare a série bruta com os cálculos, não somente com o resumo.

Teste destino inválido, DNS sem resolução, ausência de resposta ICMP e cancelamento. Erro local não deve se transformar automaticamente em perda do caminho; ICMP filtrado não comprova perda no VALORANT. Registre o destino autorizado e as condições. Teste de latência sob carga não está implementado.

## 6. Alteração única e restauração

Comece pelo plano de energia, cuja operação só seleciona um plano existente. Registre GUID/nome original e destino e mantenha ambos disponíveis.

1. Abra a pré-visualização e confirme que original, destino, componente, risco e recuperação estão visíveis.
2. Recuse UAC: não deve haver escrita; o registro deve preservar estado e backup sem informar sucesso.
3. Em uma nova tentativa autorizada, confirme que o registro `Prepared` foi persistido antes da escrita e que `Applied` só aparece após releitura do estado.
4. Use Restaurar, confirme o valor real original e estado `Restored`. Repetir restauração de valor já original deve ser idempotente.
5. Faça uma alteração externa deliberada em cenário de teste: o programa deve apontar conflito ao tentar restaurar, sem sobrescrever o terceiro valor.

Para uma propriedade do I219-V, escolha **somente** uma propriedade escalar com enumeração válida no driver real. Anote InterfaceGuid, RegistryKeyword e valores original/destino. A operação `-NoRestart` verifica o valor persistido; teste ativação separadamente e registre qualquer reinício manual necessário. Não extrapole essa prova para outras propriedades, drivers ou benefício em jogos.

Uma falha ao acessar/gravar o banco deve impedir a aplicação. Na recuperação após interrupção, registros `Prepared`/`RestorePrepared` preservam original/destino e não são repetidos automaticamente. Faça essa prova somente em ambiente de teste controlado; confira as instruções do histórico. Valores de planos ou drivers removidos podem exigir recuperação manual.

## 7. DPC/ISR real e limpeza

1. Acione captura manual de 30 s, confirme a explicação de custo e autorize UAC. Registre o nome exclusivo `PCAnalyzer-DpcIsr-*` criado **por esta execução**.
2. Reproduza a condição conhecida e examine contagens, unidade µs, CPU/tipo, média/máximo e percentis disponíveis. Compare com um trace de referência produzido pelo WPR e analisado no WPA, mantendo condições/janelas documentadas.
3. Confirme que atribuição de módulo vem de uma faixa de imagem kernel observada. Sem rundown/mapeamento ou com perdas, o módulo deve permanecer desconhecido e a análise parcial deve ser identificada.
4. Cancele a captura e repita término normal/falha de permissão. Confira com ferramentas ETW do Windows que somente a sessão própria foi encerrada e que sessões WPR/ETW alheias continuam ativas.
5. Importe um ETL real com eventos relevantes. Compare resumos com WPA e confirme que o arquivo original foi preservado. ETL sem DPC/ISR retorna dados insuficientes.

Live capture não grava um ETL bruto integral: os dados processados têm limites. Registre eventos perdidos, limites e percentis ausentes por amostra insuficiente/incompleta. DPC/ISR representa execução de kernel, não input lag físico nem prova isolada de culpa de um driver.

Se um encerramento forçado deixar sessão órfã, identifique o **nome exato registrado** e encerre somente essa sessão própria por uma ferramenta ETW autorizada. Nunca encerre `NT Kernel Logger`, sessões WPR ou outros nomes presumidos para limpar o aplicativo. Correlacionar automaticamente esses eventos com frames/scheduling permanece pendente.

## 8. PresentMon e anticheat

Obtenha o executável oficial e registre versão/assinatura/procedência. Primeiro valide uma aplicação gráfica permitida, sem anticheat. Selecione o executável e PID em execução, faça captura de 30 s, confira CSV real e streams separados por processo/swapchain/tipo/segmento. FPS usa `1000/média(frametime)`; lows e percentis têm mínimos de amostra, e falta de apresentação continua desconhecida.

Compare uma importação desse CSV com as estatísticas calculadas independentemente. Confira unidades e timestamps do esquema real: timestamps relativos não devem receber UTC inventado. Teste CLI desconhecida, PID encerrado, permissão insuficiente, CSV parcial e cancelamento; preserve resultados parciais e verifique somente a sessão própria `PCAnalyzer-Gaming-*`.

Depois, considere VALORANT somente dentro das políticas atuais do jogo/Vanguard e registre o resultado real. A aplicação não injeta código nem desativa anticheat, e isso **não garante compatibilidade**. Captura negada deve ser reportada; não contorne a política para completar o teste. Importar CSV produzido por uma execução permitida é alternativa suportada.

## 9. Laboratório manual

Confirme que cada teste exige ação explícita e apresenta duração/recursos/limitações antes do início. CPU atual: um worker SHA-256 por 5 s. RAM: buffer de 32 MiB, quatro passagens; não certifica integridade física. Disco: leitura de até 64 MiB de arquivo existente, afetada pelo cache; confira que nenhum dado de benchmark foi gravado. Cancelamento deve manter a interface responsiva e encerrar o trabalho nos pontos suportados. Não há teste CPU multithread/GPU/stress/gravação implementado.

## 10. Histórico, comparação, exportação e falhas

Salve e reabra uma sessão real e reinicie a aplicação. Confira valores, fonte, unidade, método, instante/offset, intervalo, disponibilidade, tipo e agregação; o valor ausente permanece `null`. As alterações criadas/atualizadas na janela devem aparecer no registro associado, com original/destino, risco, resultado e estado de restauração; uma sessão histórica preserva o conjunto salvo, sem atualizar silenciosamente sua narrativa. Exporte JSON/CSV/HTML e compare os valores numéricos e o diário com o banco. CSV completo é `path,type,value` e mantém cada campo por JSON Pointer, inclusive `/changes/0/state`. HTML deve escapar texto, mostrar o diário em tabela e funcionar sem recursos externos.

Com anonimização padrão, confira hostname/usuário, série/UUID/identificadores, IP/MAC e caminhos pessoais em **todas** as partes do arquivo, incluindo fonte/chave/limitação/evidência. A sessão original no banco não deve ser alterada. Revise textos livres antes de compartilhar.

Compare sessões equivalentes e depois uma sessão com carga, intervalo ou hardware diferente. Conferir avisos, quantidades de amostras, média/p95 e ausência de deltas quando a fonte/método/unidade ou dados não permitem comparação. Os resultados devem permanecer descritivos, sem concluir causalidade.

Teste falha controlada de acesso ao banco/log/exportação e pouco espaço somente em ambiente de teste apropriado. Não deve haver confirmação de gravação/restauração quando ela falha. Sensor/contador/ferramenta ausente deve afetar a leitura individual, não encerrar toda a aplicação. Confira logs limitados/rotativos e mensagens sem credenciais ou conteúdo bruto sensível.

## Fechamento da aceitação

Somente marque um recurso como validado em Windows quando houver execução e evidência do cenário correspondente. Registre divergências encontradas e corrija/reexecute o teste afetado. A próxima entrega deve atualizar este documento com resultados reais, sem substituir a compilação/testes Linux pela comprovação de hardware Windows.
