# PC Performance Analyzer

Aplicativo desktop em C#/.NET e WPF para auditar configurações de performance do Windows, interpretar medições reais e comparar snapshots e sessões. Esta versão é exclusivamente de leitura e consultoria: não aplica nem restaura ajustes operacionais. Funciona localmente, sem conta ou serviço em nuvem. A interface e as operações privilegiadas ficam em processos separados.

Esta entrega contém a fundação funcional e integrações reais implementadas, com testes de lógica, persistência e processamento de dados. **As integrações NVIDIA, dispositivos físicos, capturas ETW/PresentMon e precisão dos sensores precisam de validação no computador alvo.** A interface e as leituras nativas verificadas no runner Windows estão discriminadas na documentação de validação. Compilar a solução em Linux não demonstra esses critérios. O estado por módulo está em [PROGRESS.md](docs/PROGRESS.md), os resultados do ambiente de desenvolvimento em [VALIDATION.md](docs/VALIDATION.md) e o roteiro de aceitação em [WINDOWS-VALIDATION.md](docs/WINDOWS-VALIDATION.md).

## Requisitos e compatibilidade

- Arquitetura de publicação: Windows x64.
- Alvo técnico solicitado para os testes: **Windows 10 Pro 22H2 x64**, Intel i7-10700K, ASUS B460, NVIDIA RTX 3060, dois módulos DDR4 e dois SSDs NVMe. A descoberta enumera os dispositivos reais; não fixa esses modelos no coletor.
- SDK de desenvolvimento: **.NET 10.0.402**, fixado em [global.json](global.json). Bibliotecas Microsoft de plataforma/SQLite usam 10.0.13. A interface usa `net10.0-windows`.
- A publicação autossuficiente inclui o runtime .NET; não requer instalar um runtime separado para abrir o executável.
- WMI, Performance Counters, NetAdapter e APIs Win32 dependem dos componentes e provedores disponíveis no Windows instalado.
- Sensores NVIDIA dependem da DLL NVML fornecida pelo driver oficial. Não há redistribuição nem instalação de driver próprio.

Windows 10 Pro 22H2 encerrou o suporte regular em **14 de outubro de 2025**. Essa edição regular não aparece na [matriz oficial de sistemas suportados pelo .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). O alvo do projeto é, portanto, um **teste de compatibilidade técnica ainda pendente**, não uma declaração de suporte oficial de ciclo de vida. Um pacote autossuficiente não muda essa condição. Para uso com suporte oficial, escolha uma edição/build incluída na matriz e valide também as integrações de hardware. Não se presume que ESU, LTSC e Windows 10 Pro regular tenham a mesma condição.

## Obter e executar a publicação

Baixe **PCAnalyzer-windows-x64.zip** em [Releases no GitHub](https://github.com/henri16bit/Otimizador-/releases). Abra a versão desejada e procure o ZIP na seção **Assets**. Os links “Source code” contêm somente o código-fonte. As primeiras versões são prévias para validação local.

Extraia o pacote Windows x64 completo para uma pasta gravável e execute:

```powershell
.\artifacts\windows-x64\PCAnalyzer.exe
```

Se o ZIP tiver sido extraído diretamente na pasta de destino, execute `PCAnalyzer.exe` nessa pasta. Mantenha juntos o executável, suas dependências e a subpasta `worker`, que contém `PCAnalyzer.Worker.exe` e as dependências do auxiliar. Não copie apenas o `.exe` da interface. A entrega é uma publicação portátil; instalador e assinatura de código estão pendentes.

A abertura inicia descoberta e **coleta passiva leve**, sem ping, benchmark, captura profunda ou alteração automática. A interface executa com privilégios normais. Operações que exigem administrador usam o auxiliar depois de uma ação explícita e do diálogo UAC do Windows. O PresentMon externo é uma ferramenta opcional e suas próprias permissões continuam se aplicando.

## Compilar, testar e publicar

Na raiz do checkout, com o SDK fixado instalado:

```powershell
dotnet restore PCAnalyzer.sln
dotnet build PCAnalyzer.sln -c Release --no-restore
dotnet test tests/PCAnalyzer.Tests/PCAnalyzer.Tests.csproj -c Release --no-build --no-restore
pwsh -File scripts/publish.ps1
```

O script de publicação prepara a interface, o auxiliar e o verificador em `artifacts/windows-x64`. A distribuição deve incluir os avisos/licenças de terceiros junto aos binários.

A publicação usa os lockfiles, verifica executáveis PE x64 e runtimes incluídos, gera um ZIP completo e atualiza `SHA256SUMS.json` e o hash do ZIP. `scripts/publish.sh` também gera um ZIP dos arquivos-fonte registrados pelo Git, incluindo arquivos novos e respeitando `.gitignore`. No Linux, exige Python 3.12+; no Windows, use PowerShell 7 (`pwsh`).

Para desenvolvimento no Windows, também é possível iniciar o projeto:

```powershell
dotnet run --project src/PCAnalyzer.App/PCAnalyzer.App.csproj
```

Uma publicação completa é o caminho recomendado para validar operações que dependem do auxiliar. Em Linux, a configuração `EnableWindowsTargeting` permite compilar WPF e preparar o pacote Windows, mas a interface e os coletores Windows não podem ser executados nesse sistema. A suíte normal usa dados de teste identificados e operações falsas nas fronteiras de hardware; não altera configurações reais nem envia ping automaticamente.

No ambiente Codex cloud, use o checkout existente em `/workspace`: cada tarefa já é isolada, e não é necessário criar um worktree. O script de preparação reutilizável está em `scripts/setup.sh`; os comandos de instalação e validação não modificam a aplicação nem geram medições de hardware Windows no Linux.

Os testes de empacotamento podem ser executados com `python -m unittest discover -s tests/package_tests -v`. A CI conserva os resultados das suítes e realiza a verificação passiva no runner Windows; sensores do computador alvo e compatibilidade Vanguard continuam exigindo validação local.

Tags `v*` acionam o workflow `Publish Windows release`, que compila, testa, abre a janela WPF no Windows e publica o ZIP e seu SHA256 no GitHub Releases como prévia. Uma nova versão só se torna pública depois do envio dos dois arquivos. Versões criadas manualmente que tenham somente os arquivos-fonte também podem receber o pacote; binários já publicados são preservados. O resultado é registrado em `refs/notes/pcanalyzer-releases` para permitir verificação por Git quando a API estiver indisponível.

Se o aplicativo encerrar ao abrir, consulte `%LOCALAPPDATA%\PCAnalyzer\logs\startup-last-error.txt`. Falhas gerenciadas de inicialização passam a mostrar uma mensagem mesmo quando o carregamento dos recursos WPF falha. Problemas anteriores ao carregamento do .NET podem ser investigados com `DOTNET_HOST_TRACE=1` e `DOTNET_HOST_TRACEFILE` apontando para um arquivo gravável; esses registros são locais e podem conter caminhos do computador.

## Uso inicial

1. Abra como usuário normal. No **Painel**, clique em **ANALISAR MEU PC** e confirme a observação. O aplicativo reúne fontes, interpreta evidências e salva um relatório. Nenhum teste de carga ou ajuste é iniciado por essa análise.
2. Em **Analisar**, escolha 15, 30 ou 60 segundos e descreva o uso atual. Ping de 20 pacotes e captura DPC/ISR de 30 segundos são opcionais, desligados por padrão e descritos na confirmação; ETW solicita UAC.
3. A conclusão aparece **na própria tela, abaixo da ação de análise**, sem redirecionamento obrigatório. **Resultados** também permite consultar o relatório completo. Evidências e detalhes técnicos ficam recolhidos; dados ausentes permanecem inconclusivos.
4. Para jogos, configure uma vez o PresentMon oficial em **Analisar**, selecione o processo aberto ou informe seu nome para aguardar a abertura. A captura só começa após confirmação. CSV existente continua em **Avançado → Jogos e frames**.
5. **Avançado** reúne Componentes, Processos, Rede, Drivers, testes controlados e Histórico. Métricas conservam origem, método, timestamp e gráfico; cada disco tem seleção própria. Em Configurações, o modo Avançado acrescenta atalhos ao menu.
6. Reabra análises salvas, compare execuções equivalentes e exporte HTML/JSON. Sessões de telemetria também podem ser exportadas em CSV. Cancelamentos e falhas parciais preservam as evidências disponíveis.
7. **Recomendações** explica configurações encontradas, evidências, riscos e formas de validar hipóteses. **Auditoria de otimizações** lê Windows, perfis NVIDIA, dispositivos, rede e armazenamento, sem executar mudanças. **Configurações** mostra fontes e reinício da coleta.

A interface usa um único menu lateral e ajusta o tamanho inicial à área útil da tela. As unidades legíveis são conversões de apresentação; valores e unidades originais permanecem nas sessões e relatórios. A RAM do resumo é derivada de memória física total/disponível sincronizadas, não de commit. A rede mostra o adaptador com maior recebimento medido, identificado no cartão; não soma tráfego possivelmente duplicado por VPNs.

Em **Avançado → Rede**, a conexão ativa e seus detalhes são lidos automaticamente. Você pode escolher outro adaptador ou atualizar a leitura. Ping continua manual. A análise geral compara contadores inicial/final da mesma conexão; contadores acumulados, reiniciados ou indisponíveis não comprovam perda durante a sessão. Todas as propriedades ficam como **Somente leitura**, inclusive quando o driver divulga valores alternativos. Gateway usa a conexão selecionada; outro destino segue a rota do Windows.

O **Histórico de diagnósticos** guarda automaticamente resultados estruturados, gráficos, erros e cancelamentos. É possível reabrir, comparar execuções do mesmo teste e exportar HTML/JSON. Uma mudança de destino, arquivo ou método impede comparação enganosa. Sessões de telemetria e seus relatórios continuam disponíveis abaixo. Dados antigos são preservados pela migração SQLite; JSONs avulsos de versões anteriores continuam na pasta `results`, acessível por **Abrir pasta de dados e logs**.

Há uma execução manual por vez. Navegar preserva a coleta e o teste. A análise coordenada usa intervalo alvo de 1 s e depois retoma o perfil passivo. O inventário detalhado não impede as primeiras leituras básicas. Fontes opcionais são isoladas, com tentativas limitadas; pausa, atraso e erros aparecem explicitamente. Capturas importadas não entram nas leituras atuais. Encerramento cancela tarefas; uma execução anterior sem fim registrado aparece como falha interrompida, com duração desconhecida.

O perfil **Leve** usa intervalo alvo de 5 s e o **Avançado**, 1 s; o intervalo pode ser ajustado entre 1 e 60 s. Ao minimizar, a cadência passa a no mínimo 15 s. A interface atualiza independentemente, aproximadamente uma vez por segundo. O buffer retém até 30.000 amostras totais, registra descartes nas notas da sessão e mostra até 600 pontos da série selecionada. O modo profundo é uma captura manual limitada, não um coletor permanente.

## Funcionalidades implementadas

| Área | Implementação e interpretação |
| --- | --- |
| Descoberta | WMI/SMBIOS, Windows/BIOS/placa/CPU/DIMMs, GPU, discos/partições/volumes/pagefile, monitores e energia. Dados ausentes ficam explícitos. |
| CPU e processos | Utilização total/lógica, fila, trocas de contexto, interrupções e CPU/working set por processo. Taxas usam o tempo decorrido; PID reutilizado não mistura a identidade do processo. |
| Memória | Memória física/disponível, commit/limite e contadores de paginação. Commit não é RAM residente e page reads não comprovam sozinho pressão de pagefile. |
| NVIDIA | NVAPI DRS: perfis base/global/aplicativos, origem herdada/predefinida/usuário e configurações públicas. Leitura do driver não prova efeito no jogo. NVML: utilização, VRAM, temperatura, clocks, potência/P-state/limitações conforme o suporte real do driver. Ausência de NVML não vira uma capacidade de VRAM inventada pelo WMI. |
| Discos | Modelos e associações Win32; identificação de barramento pelo provedor Storage quando disponível; I/O, fila e latência média de cada disco. Sem I/O, latência fica ausente. |
| Rede | Adaptadores reais, estatísticas, velocidade, driver e propriedades NetAdapter com valores válidos. Guias explicam efeitos sem prometer melhoria. |
| Ping manual | Na interface, 20 sondas ICMP para destino escolhido ou gateway. RTT min/média/max/p95, ausência de resposta, jitter e série temporal; nenhum tráfego automático na abertura. |
| DPC/ISR | Captura kernel ETW manual de 30 s pelo auxiliar elevado, importação de ETL existente, resumos por CPU/tipo/módulo, timeline/buckets limitados, contagem/média/máximo/percentis conforme amostra. |
| Jogos | Importação de CSV PresentMon 1.x/2.x/3.x, streams separados, frametimes/FPS/p95/p99/p99,9 e lows quando há amostra suficiente. Captura manual de 30 s com executável/PID escolhido e CLI compatível. |
| Triagem | CPU sustentada, memória disponível baixa com commit elevado, respostas ICMP, recorrência DPC e variação de frames. Correlação temporal exige UTC conhecido e amostras próximas; não prova causalidade. Achados incluem evidências, confiança, próximos passos e limites. |
| Auditoria/consultoria | Catálogo extensível de Registro/MMCSS/IFEO/energia/mouse, processos/serviços, NVIDIA, MSI/IRQ/afinidade/USB, RAM/discos e rede. Valores brutos e fontes separados das conclusões. APIs legadas de escrita e restauração são bloqueadas. |
| Test Lab | CPU single-worker SHA-256 por 5 s; buffer RAM de 32 MiB; teste adicional de acesso dependente e leitura/escrita/cópia em buffers virtuais de 1/16/64 MiB; leitura de até 64 MiB de arquivo existente. Início manual, progresso e cancelamento; não há gravação de benchmark. |
| Histórico/relatórios | SQLite transacional com WAL, sessão completa com metadados, diário de alterações, comparação descritiva, JSON/CSV/HTML e anonimização padrão. Logs locais JSON Lines com rotação. |

As regras de CPU examinam pelo menos 10 amostras e uma janela de pelo menos 8 s: utilização ≥90% em ≥80% das amostras recentes é uma condição de **triagem**, não prova de gargalo ou defeito. A regra de RAM combina memória disponível baixa e commit elevado repetidos. Não existe ainda um catálogo de desempenho esperado por hardware nem um motor completo de causalidade. Operar RAM a 2933 MT/s no contexto B460/i7-10700K não é classificado automaticamente como defeito; WMI não comprova SPD, timings ou dual-channel.

## Ferramentas auxiliares e permissões

- **NVML:** fornecida pelo driver NVIDIA oficial. O carregamento usa localizações conhecidas, sem buscar uma DLL no diretório de trabalho.
- **PresentMon:** obtenha o executável oficial no [projeto Intel PresentMon](https://github.com/GameTechDev/PresentMon). A aplicação verifica Authenticode pelo Windows e reconhece os editores Intel/Microsoft antes de inspecionar a CLI. Executáveis sem assinatura reconhecida permanecem indisponíveis; importação CSV continua disponível. Isso não é autorização de anticheat. O caminho é salvo localmente e verificado novamente antes de cada captura.
- **ETW:** captura DPC/ISR requer administrador, fornecido pelo auxiliar UAC após confirmação. Importação de ETL preserva o original; o live capture armazena dados processados limitados, sem um arquivo ETL bruto de toda a sessão.
- **WPR/WPA:** ferramentas opcionais para validação independente e produção de ETLs. A aplicação não instala nem mantém uma sessão WPR automática.
- **NetAdapter/PowrProf:** somente consulta aos recursos do Windows. Nenhum setter de propriedade ou plano é permitido nesta versão. O estado persistido não é tratado como prova de ativação no driver.

Nenhuma integração injeta código no jogo, instala driver kernel próprio ou desativa Vanguard. Compatibilidade com VALORANT/Vanguard não foi demonstrada neste ambiente. Consulte a política atual do jogo e valide primeiro uma aplicação gráfica permitida.

O auxiliar atual requer elevação UAC da **mesma conta Windows**, membro do grupo Administradores usando a interface sem elevação. Elevação por credenciais de outra conta não tem IPC implementado nesta versão; nessa situação a coleta passiva continua disponível e a operação privilegiada não é confirmada. Pastas de requisição redirecionadas por reparse points também são recusadas.

Para verificar leituras e relatórios reais sem alterar configurações, execute `verify\PCAnalyzer.Verify.exe` manualmente e consulte [o roteiro do verificador](docs/verify.md). Esse verificador não valida a interface, ETW nem PresentMon.

## Histórico, privacidade e recuperação

Dados locais ficam em `%LocalAppData%\PCAnalyzer`: `history.db`, logs, resultados/capturas e preferências. O banco preserva snapshots completos e registros históricos de versões anteriores, apenas para consulta. A anonimização ocorre na cópia exportada; não apaga dados do histórico. O módulo não faz upload de telemetria.

JSON e HTML contêm a sessão completa, incluindo alterações criadas ou atualizadas dentro da janela da sessão, valores original/solicitado, risco, resultados e estado de restauração. O HTML também mostra esse diário em tabela. Sessões salvas preservam o conjunto associado naquele registro. CSV completo tem colunas `path,type,value`, preservando todos os campos por caminho JSON Pointer; `/samples/0/value` e `/changes/0/state`, por exemplo. A API também oferece `ExportMetricCsv` para uma tabela exclusiva de telemetria. Comparação exibe número de amostras, médias e p95, diferenças e avisos de carga/intervalo/hardware/fonte diferentes. Métrica ausente permanece ausente; uma diferença entre duas sessões não comprova benefício de uma otimização.

O histórico de alterações das versões anteriores continua consultável. Os botões e caminhos de aplicação/restauração foram retirados; a API e o auxiliar também rejeitam essas operações. Comparações de configurações exigem duas leituras compatíveis. Mudanças realizadas externamente pelo usuário não são executadas pelo PC Analyzer.

## Limitações e próximos módulos

Permanecem pendentes: validação real no Windows alvo; temperatura/frequência efetiva e throttling de CPU; leitura SPD/timings/XMP/canais; SMART bruto e métricas NVMe não expostas pelo provedor Storage (saúde/temperatura são lidas quando reportadas); atribuição de threads/agendamento por ETW; alinhamento automático de capturas em relógios relativos; baseline de desempenho esperado por modelo; benchmark CPU multithread, GPU/stress e gravação de disco; rede sob carga; installer/assinatura e medição de overhead em partidas reais.

Relatórios de teste incluem indicadores, séries retidas, evidências, recomendações, limitações e contexto da telemetria. O resultado bruto separado continua preservado em `results`. Correlação examina janelas UTC de ±250 ms apenas quando frames têm data/hora e offset conhecidos; médias de até 1 s e eventos ETW são apresentados como proximidade temporal, sem afirmar causalidade. CSV com QPC/data local permite informar frequência/offset da captura. Relógios relativos sem âncora ficam explicitamente sem correlação. Não existe medição de input lag físico clique-para-fóton, polling real do mouse ou escore universal de latência do PC.

## Estrutura e documentação

| Projeto | Responsabilidade |
| --- | --- |
| `PCAnalyzer.App` | WPF/MVVM, gráficos, histórico e comandos manuais. A UI usa uma abstração de backend. |
| `PCAnalyzer.Worker` | Operações enumeradas privilegiadas; recebe requisição validada, não comandos arbitrários. |
| `PCAnalyzer.Core` | Contratos, estados, metadados, estatística, triagem e testes controlados. |
| `PCAnalyzer.Hardware` | Descoberta e telemetria Windows/NVML. |
| `PCAnalyzer.Diagnostics` | Captura/importação ETW e processamento DPC/ISR. |
| `PCAnalyzer.Gaming` | Captura/importação PresentMon e estatística de frames. |
| `PCAnalyzer.Network` | Descoberta/NetAdapter/ping manual e explicações de propriedades. |
| `PCAnalyzer.Optimization` | Compatibilidade de registros/prévias legadas; aplicação e restauração bloqueadas pela política somente leitura. |
| `PCAnalyzer.Storage` | SQLite e logs estruturados locais. |
| `PCAnalyzer.Reports` | Comparação, anonimização e exportação. |
| `PCAnalyzer.Tests` | Testes automatizados sem alterações reais no sistema. |

Documentação específica: [hardware](docs/hardware.md), [ETW](docs/tracing.md), [PresentMon](docs/gaming.md), [rede/consulta](docs/network-optimization.md) e [histórico/relatórios](docs/storage-reports.md). Licenças e referências de terceiros: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Auditorias novas: [escopo e pendências](docs/PERFORMANCE-CONSULTANCY.md), [Windows](docs/WINDOWS-AUDIT-SOURCES.md), [NVIDIA](docs/NVIDIA-AUDIT.md), [dispositivos](docs/DEVICE-AUDIT.md) e [rede/armazenamento](docs/NETWORK-STORAGE-AUDIT.md).
