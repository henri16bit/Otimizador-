# PC Performance Analyzer

Aplicativo desktop em C#/.NET e WPF para coletar indicadores reais do Windows, examinar evidências, registrar sessões e comparar medições antes/depois de uma alteração autorizada. Funciona localmente, sem conta ou serviço em nuvem. A interface e as operações privilegiadas ficam em processos separados.

Esta entrega contém a fundação funcional e integrações reais implementadas, com testes de lógica, persistência e processamento de dados. **A execução da interface, a precisão dos sensores, as capturas ETW/PresentMon e a aplicação/restauração de configurações ainda precisam de validação no computador Windows.** Compilar a solução em Linux não demonstra esses critérios. O estado por módulo está em [PROGRESS.md](docs/PROGRESS.md), os resultados do ambiente de desenvolvimento em [VALIDATION.md](docs/VALIDATION.md) e o roteiro de aceitação em [WINDOWS-VALIDATION.md](docs/WINDOWS-VALIDATION.md).

## Requisitos e compatibilidade

- Arquitetura de publicação: Windows x64.
- Alvo técnico solicitado para os testes: **Windows 10 Pro 22H2 x64**, Intel i7-10700K, ASUS B460, NVIDIA RTX 3060, dois módulos DDR4 e dois SSDs NVMe. A descoberta enumera os dispositivos reais; não fixa esses modelos no coletor.
- SDK de desenvolvimento: **.NET 10.0.402**, fixado em [global.json](global.json). Bibliotecas Microsoft de plataforma/SQLite usam 10.0.13. A interface usa `net10.0-windows`.
- A publicação autossuficiente inclui o runtime .NET; não requer instalar um runtime separado para abrir o executável.
- WMI, Performance Counters, NetAdapter e APIs Win32 dependem dos componentes e provedores disponíveis no Windows instalado.
- Sensores NVIDIA dependem da DLL NVML fornecida pelo driver oficial. Não há redistribuição nem instalação de driver próprio.

Windows 10 Pro 22H2 encerrou o suporte regular em **14 de outubro de 2025**. Essa edição regular não aparece na [matriz oficial de sistemas suportados pelo .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). O alvo do projeto é, portanto, um **teste de compatibilidade técnica ainda pendente**, não uma declaração de suporte oficial de ciclo de vida. Um pacote autossuficiente não muda essa condição. Para uso com suporte oficial, escolha uma edição/build incluída na matriz e valide também as integrações de hardware. Não se presume que ESU, LTSC e Windows 10 Pro regular tenham a mesma condição.

## Obter e executar a publicação

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

## Uso inicial

1. Abra a aplicação como usuário normal e aguarde a descoberta. Consulte a tabela de capacidades para distinguir leitura disponível, sem suporte, ferramenta ausente e falha.
2. Use o Dashboard e as seções CPU, GPU, RAM, NVMe, Rede e Processos para examinar os dados. Selecione uma série para ver seu gráfico, fonte, unidade, método, intervalo e limitações.
3. Informe a carga observada e notas: por exemplo, repouso, cena de jogo reproduzível ou uma aplicação específica. Aguarde uma janela suficiente antes de interpretar a triagem.
4. Salve a sessão no histórico. Registre sessões diferentes para condições diferentes; anote mudanças de intervalo, carga e configuração.
5. Inicie testes ativos apenas pelos respectivos comandos. Execute uma mudança por vez, meça novamente sob condições equivalentes e use Performance Comparison.
6. Exporte a sessão em JSON, CSV ou HTML. A anonimização vem ativada; confira textos livres antes de compartilhar.

O perfil **Leve** usa intervalo alvo de 5 s e o **Avançado**, 1 s; o intervalo pode ser ajustado entre 1 e 60 s. Ao minimizar, a cadência passa a no mínimo 15 s. A interface atualiza independentemente, aproximadamente uma vez por segundo. O buffer retém até 30.000 amostras totais, registra descartes nas notas da sessão e mostra até 600 pontos da série selecionada. O modo profundo é uma captura manual limitada, não um coletor permanente.

## Funcionalidades implementadas

| Área | Implementação e interpretação |
| --- | --- |
| Descoberta | WMI/SMBIOS, Windows/BIOS/placa/CPU/DIMMs, GPU, discos/partições/volumes/pagefile, monitores e energia. Dados ausentes ficam explícitos. |
| CPU e processos | Utilização total/lógica, fila, trocas de contexto, interrupções e CPU/working set por processo. Taxas usam o tempo decorrido; PID reutilizado não mistura a identidade do processo. |
| Memória | Memória física/disponível, commit/limite e contadores de paginação. Commit não é RAM residente e page reads não comprovam sozinho pressão de pagefile. |
| NVIDIA | NVML: utilização, VRAM, temperatura, clocks, potência/P-state/limitações conforme o suporte real do driver. Ausência de NVML não vira uma capacidade de VRAM inventada pelo WMI. |
| Discos | Modelos e associações Win32; identificação de barramento pelo provedor Storage quando disponível; I/O, fila e latência média de cada disco. Sem I/O, latência fica ausente. |
| Rede | Adaptadores reais, estatísticas, velocidade, driver e propriedades NetAdapter com valores válidos. Guias explicam efeitos sem prometer melhoria. |
| Ping manual | Na interface, 20 sondas ICMP para destino escolhido ou gateway. RTT min/média/max/p95, ausência de resposta, jitter e série temporal; nenhum tráfego automático na abertura. |
| DPC/ISR | Captura kernel ETW manual de 30 s pelo auxiliar elevado, importação de ETL existente, resumos por CPU/tipo/módulo, timeline/buckets limitados, contagem/média/máximo/percentis conforme amostra. |
| Jogos | Importação de CSV PresentMon 1.x/2.x/3.x, streams separados, frametimes/FPS/p95/p99/p99,9 e lows quando há amostra suficiente. Captura manual de 30 s com executável/PID escolhido e CLI compatível. |
| Triagem | Regras limitadas de CPU sustentada, memória disponível baixa com commit elevado, dados insuficientes e contexto da plataforma RAM. Achados incluem evidências/confiança/recomendação/verificação/reversão. |
| Otimizações | Pré-visualização, backup durável, mudança de plano de energia existente e propriedade escalar de adaptador suportada pelo driver, releitura, restauração e detecção de conflito. |
| Test Lab | CPU single-worker SHA-256 por 5 s; buffer RAM de 32 MiB em quatro passagens; leitura de até 64 MiB de arquivo existente. Início manual, progresso e cancelamento; não há gravação de benchmark. |
| Histórico/relatórios | SQLite transacional com WAL, sessão completa com metadados, diário de alterações, comparação descritiva, JSON/CSV/HTML e anonimização padrão. Logs locais JSON Lines com rotação. |

As regras de CPU examinam pelo menos 10 amostras e uma janela de pelo menos 8 s: utilização ≥90% em ≥80% das amostras recentes é uma condição de **triagem**, não prova de gargalo ou defeito. A regra de RAM combina memória disponível baixa e commit elevado repetidos. Não existe ainda um catálogo de desempenho esperado por hardware nem um motor completo de causalidade. Operar RAM a 2933 MT/s no contexto B460/i7-10700K não é classificado automaticamente como defeito; WMI não comprova SPD, timings ou dual-channel.

## Ferramentas auxiliares e permissões

- **NVML:** fornecida pelo driver NVIDIA oficial. O carregamento usa localizações conhecidas, sem buscar uma DLL no diretório de trabalho.
- **PresentMon:** obtenha o executável oficial no [projeto Intel PresentMon](https://github.com/GameTechDev/PresentMon). Selecione esse executável e um PID em execução. A aplicação verifica a ajuda/CLI; isso não é verificação de assinatura ou autorização de anticheat. Captura incompatível ou sem permissão fica indisponível; importação CSV continua disponível.
- **ETW:** captura DPC/ISR requer administrador, fornecido pelo auxiliar UAC após confirmação. Importação de ETL preserva o original; o live capture armazena dados processados limitados, sem um arquivo ETL bruto de toda a sessão.
- **WPR/WPA:** ferramentas opcionais para validação independente e produção de ETLs. A aplicação não instala nem mantém uma sessão WPR automática.
- **NetAdapter/PowrProf:** recursos do Windows. A interface enumera apenas as propriedades e planos realmente encontrados. Alterar propriedade usa `-NoRestart`; a leitura confirmada é da configuração persistida, não prova de ativação efetiva do driver.

Nenhuma integração injeta código no jogo, instala driver kernel próprio ou desativa Vanguard. Compatibilidade com VALORANT/Vanguard não foi demonstrada neste ambiente. Consulte a política atual do jogo e valide primeiro uma aplicação gráfica permitida.

O auxiliar atual requer elevação UAC da **mesma conta Windows**, membro do grupo Administradores usando a interface sem elevação. Elevação por credenciais de outra conta não tem IPC implementado nesta versão; nessa situação a coleta passiva continua disponível e a operação privilegiada não é confirmada. Pastas de requisição redirecionadas por reparse points também são recusadas.

Para verificar leituras e relatórios reais sem alterar configurações, execute `verify\PCAnalyzer.Verify.exe` manualmente e consulte [o roteiro do verificador](docs/verify.md). Esse verificador não valida a interface, ETW nem PresentMon.

## Histórico, privacidade e recuperação

Dados locais ficam em `%LocalAppData%\PCAnalyzer`: `history.db`, logs, resultados/capturas e preferências. O banco preserva dados completos e valores originais necessários à reversão. A anonimização ocorre na cópia exportada; não apaga dados do histórico. O módulo não faz upload de telemetria.

JSON e HTML contêm a sessão completa, incluindo alterações criadas ou atualizadas dentro da janela da sessão, valores original/solicitado, risco, resultados e estado de restauração. O HTML também mostra esse diário em tabela. Sessões salvas preservam o conjunto associado naquele registro. CSV completo tem colunas `path,type,value`, preservando todos os campos por caminho JSON Pointer; `/samples/0/value` e `/changes/0/state`, por exemplo. A API também oferece `ExportMetricCsv` para uma tabela exclusiva de telemetria. Comparação exibe número de amostras, médias e p95, diferenças e avisos de carga/intervalo/hardware/fonte diferentes. Métrica ausente permanece ausente; uma diferença entre duas sessões não comprova benefício de uma otimização.

Antes de uma mudança, o motor salva o original e a intenção no banco. Uma falha do backup impede a escrita. Para reverter:

1. Abra o histórico de alterações, selecione a alteração e confira componente, valor original, valor aplicado e estado.
2. Use **Restaurar** e autorize a operação quando solicitado. A aplicação relê o valor real e confirma o resultado.
3. Desfaça mudanças sucessivas na ordem inversa. Se outra ferramenta modificou o mesmo valor, a restauração registra conflito em vez de sobrescrevê-lo.
4. Depois de uma interrupção, registros `Prepared`/`RestorePrepared` não são reaplicados automaticamente. Examine o componente e use a recuperação explícita.
5. Se a reversão falhar, siga as instruções do registro: plano original em Opções de Energia; propriedade do adaptador correto no Gerenciador de Dispositivos → Avançado. Use o valor original do diário. Um plano/driver removido pode tornar necessária recuperação manual.

Reiniciar o adaptador ou computador é uma ação separada e pode ser necessário para ativar uma propriedade. Nenhum reinício global de rede é feito automaticamente.

## Limitações e próximos módulos

Permanecem pendentes: validação real no Windows alvo; temperatura/frequência efetiva e throttling de CPU; leitura SPD/timings/XMP/canais; SMART/desgaste/temperatura NVMe; atribuição de threads/agendamento por ETW; correlação automática entre frames, ETW e telemetria; baseline de desempenho esperado por modelo; benchmark CPU multithread, GPU/stress e gravação de disco; rede sob carga; installer/assinatura e medição de overhead em partidas reais.

Os relatórios de sessão já incluem as alterações associadas, mas não incorporam automaticamente todo artefato separado de ETW, frames, laboratório e comparação numa narrativa única. Esses resultados locais têm sua própria persistência, e sua integração em um relatório de investigação completo é trabalho futuro. Não existe medição de input lag físico clique-para-fóton, polling real do mouse ou escore universal de latência do PC.

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
| `PCAnalyzer.Optimization` | Pré-visualização, backup, aplicação, verificação e restauração. |
| `PCAnalyzer.Storage` | SQLite e logs estruturados locais. |
| `PCAnalyzer.Reports` | Comparação, anonimização e exportação. |
| `PCAnalyzer.Tests` | Testes automatizados sem alterações reais no sistema. |

Documentação específica: [hardware](docs/hardware.md), [ETW](docs/tracing.md), [PresentMon](docs/gaming.md), [rede/restauração](docs/network-optimization.md) e [histórico/relatórios](docs/storage-reports.md). Licenças e referências de terceiros: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
