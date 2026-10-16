# Validação executada

## Análise coordenada — 16/10/2026

Versão **v0.4.0-preview.3**, código `5626928abe795ca0a7ff4f00295130fe3caba48c`: [execução Windows aprovada](https://github.com/henri16bit/Otimizador-/actions/runs/38980121940) e [ZIP publicado](https://github.com/henri16bit/Otimizador-/releases/tag/v0.4.0-preview.3).

- Reconstrução Release com avisos tratados como erros; **243 testes C# aprovados**, nenhum falhou ou foi ignorado. Suíte também executada no Linux (`artifacts/test-results/review-analysis.trx`). Cinco testes Python de empacotamento e cinco cenários PowerShell de publicação aprovados.
- **22 cenários WPF** aprovados: coleta antes do inventário, menu simples, progresso/navegação, cancelamento/falha, relatório por componente, GPU ausente inconclusiva, persistência/reabertura, rede automática, limpeza de resultado antigo, importações isoladas, última tentativa falha visível e observação selecionada preservada. Nenhum erro de binding foi registrado. As fronteiras de hardware desses cenários usam fixtures explícitas.
- Layout WPF medido, organizado e renderizado fora da tela em **1366×768 e 1920×1080**, preservando recursos e bindings da janela. A área de trabalho do runner limita o HWND; essas capturas não equivalem a teste em dois monitores ou DPI diferentes. A janela real foi validada no tamanho disponível e no tamanho compacto.
- **Verificação passiva real no runner Windows:** três lotes, CPU/RAM/commit e processo próprio válidos nos dois últimos; 361 métricas persistidas. SQLite recarregado sem diferenças, JSON preservando valores/metadados, CSV equivalente ao JSON e HTML equivalente à sessão salva. Nenhuma NVIDIA foi identificada: GPU não foi validada por esse teste.
- Pacote autossuficiente aberto: **10 páginas e 7 abas internas**, tamanho compacto, saída zero. ZIP e SHA256 confirmados no GitHub; ZIP de 148.587.058 bytes, SHA256 `ac3d436b58d3ca96c66dbd283b4dad1c13ed0a90545113d7461a388bffa13bea`.

As tentativas `v0.4.0-preview.1` e `.2` não publicaram binários: a primeira detectou seleção de duração ainda não renderizada; a segunda revelou o limite da área de trabalho do runner. O seletor recebeu rótulo direto e espera pelo ciclo visual; a validação das resoluções passou a usar dimensões reais do layout WPF fora da tela. As asserções permaneceram ativas.

Ainda dependem do PC alvo: Windows 10 Pro 22H2, NVML/GPU Engine e precisão dos sensores, UAC/ETW real, PresentMon em jogo, overhead, escalas DPI e aplicação/restauração no driver de rede. O teste do runner não certifica saúde física nem ganhos de desempenho. Veja as fases e arquivos em [DIAGNOSTIC-REFACTOR-AUDIT.md](DIAGNOSTIC-REFACTOR-AUDIT.md).

## Registro anterior — 15/10/2026

Ambiente: Debian 13 x64, SDK .NET 10.0.402 (runtime 10.0.13). O SDK foi baixado de `builds.dotnet.microsoft.com` e verificado por SHA-512 contra o manifesto oficial. Pacotes NuGet restaurados com verificação normal de TLS/assinaturas; versões e hashes estão nos lockfiles.

## Diagnósticos integrados e rede

A revisão adiciona histórico estruturado de testes, estados/progresso no dashboard, ajuda de métricas, rede por adaptador e interpretação contextual. A solução agora tem 13 projetos, incluindo um executável separado de cenários WPF. O relatório por fase está em [UX-AUDIT.md](UX-AUDIT.md).

Validação local: restore travado, compilação Release com avisos tratados como erros e **142 testes C#**, sem falhas ou ignorados. Resultado: `artifacts/test-results/diagnostics-refactor.trx`. Os cinco testes Python de pacote e cinco cenários PowerShell de publicação são verificados separadamente. Nenhuma configuração de hardware é alterada por essas suítes.

O workflow de release bloqueia a publicação até executar no Windows os cenários WPF de progresso, navegação durante teste, conclusão sem troca de página, cancelamento, falha, reabertura do SQLite e rede com propriedade somente leitura. Depois abre o aplicativo empacotado nas oito páginas e sete abas internas, inclusive tamanho compacto. Os registros por commit ficam em `refs/notes/pcanalyzer-releases`; as capturas ficam nos artefatos e nas notas `pcanalyzer-ui-preview`, `pcanalyzer-network-preview` e `pcanalyzer-flow-preview`. Os cenários de UI usam fixtures explícitas, sem coleta ou alteração real do sistema.

Comando dos cenários em Windows, após o build:

```powershell
./scripts/test-ui.ps1
```

A aprovação de um workflow confirma apenas os checks executados naquele commit. Sensores, driver da rede, UAC, ETW/PresentMon, overhead e Windows 10 Pro 22H2 do computador alvo continuam sujeitos à validação local.

### Verificação Windows e publicação confirmadas

Versão **v0.3.0-preview.3**, commit `cded4d8111632c04b0432642db3c8c1f43ce1a02`: [execução no Windows](https://github.com/henri16bit/Otimizador-/actions/runs/38729726505). Restore, compilação sem avisos, 142 testes C#, cinco testes de pacote, cinco cenários de publicação, cenários WPF, empacotamento, smoke e publicação terminaram com sucesso.

Os **dez cenários WPF aprovados** verificaram nomes legíveis nas listas, recuperação após interrupção, progresso visível, navegação durante execução, conclusão sem trocar de página, cancelamento, falha, reabertura do SQLite, leitura de rede com propriedade indisponível e sensores ausentes. O smoke do pacote confirmou **oito páginas, sete abas internas e tamanho compacto**, sem iniciar coleta de hardware. As capturas finais da visão geral e da rede foram inspecionadas; a seleção agora mostra o nome legível, sem texto interno de records.

A [release pública](https://github.com/henri16bit/Otimizador-/releases/tag/v0.3.0-preview.3) contém `PCAnalyzer-windows-x64.zip` (148.403.615 bytes) e `PCAnalyzer-windows-x64.zip.sha256`, ambos confirmados como `uploaded`. SHA256 do ZIP: `e6f29374b0dd03b2d21c6a30c74c87c34d5cdebcd8875d5eabc1fc92817d8cbc`. O registro foi obtido via `refs/notes/pcanalyzer-releases`; a captura de rede com dados de cenário está em `refs/notes/pcanalyzer-network-fixture-preview`. Isso comprova os fluxos testados no runner, não o hardware do usuário.

## Histórico: revisão anterior da interface (14/10)

A suíte portátil passou a **116 casos aprovados**, incluindo apresentação de unidades sem modificar as amostras, RAM física sincronizada, leituras indisponíveis, precisão de valores pequenos, escala dos gráficos e recebimento identificado de um único adaptador. Os cinco testes Python de pacote e os cinco cenários PowerShell isolados de publicação também passaram. O resultado C# está em `artifacts/test-results/interface.trx`.

O workflow de release verifica no Windows as oito páginas, sete abas internas, atalhos, tamanho compacto, abertura e fechamento, e guarda capturas PNG em seus artefatos. O resultado da execução é registrado em `refs/notes/pcanalyzer-releases`; uma captura da visão geral pode ser obtida por `refs/notes/pcanalyzer-ui-preview`. A API GitHub continua inacessível neste ambiente, mas o resultado remoto pode ser conferido pelo Git.

A versão anterior `v0.1.0-preview.4` já teve abertura/renderização/fechamento confirmados no runner Windows, com [registro da execução](https://github.com/henri16bit/Otimizador-/actions/runs/38617713687) e ZIP/SHA256 publicados. Isso não valida sensores, alterações administrativas, ETW/PresentMon ou o Windows 10 Pro 22H2 do computador alvo.

## Histórico: validação da implementação inicial

| Verificação | Resultado observado |
| --- | --- |
| Solução completa, Release | 12 projetos compilados, incluindo XAML/WPF, auxiliar e verificador. Zero erros e zero avisos. |
| Suíte portátil após revisão | **103 executados, 103 aprovados, 0 falhas, 0 ignorados**. Resultado da revisão em `artifacts/test-results/review.trx`; execução inicial tinha 88 casos. |
| Empacotamento | 5 testes Python aprovados: hashes atualizados, ZIP legível, auxiliar obrigatório, arquitetura e runtimes, prevenção de ZIP recursivo. |
| Dependências NuGet | Consulta de vulnerabilidades conhecidas, incluindo transitivas, não retornou pacotes vulneráveis. Isso se limita à base de avisos disponível no NuGet. |
| Persistência e relatórios | Testes verificam SQLite/WAL, recarga de valores/metadados/diário, concorrência, anonimização, CSV/JSON/HTML e exportação interrompida. |
| Estatística e diagnóstico | Testes verificam percentis R-7, jitter com lacunas, CPU por lógico, pressão de memória contextual e proteção contra inferência de throttling/XMP. |
| ETW e frames | Testes processam eventos/CSV controlados: atribuição sem suposições, timeline limitada, percentis completos, streams separados, unidades, drops e frametimes. Não são capturas de hardware real. |
| Reversão | Operações isoladas verificam backup antes da escrita, valor esperado original, conflito externo, falha parcial, cancelamento, verificação e restauração. Nenhuma configuração real foi alterada pela suíte. |
| Qualidade da telemetria | Testes verificam estados sem suporte, cancelamento/disposal, layout ABI e latência de disco indisponível sem I/O. |
| `PCAnalyzer.Verify --help` | Executado; uso exibido corretamente. |
| Verificador no Linux | Executado: código 2 e mensagem explícita de plataforma incompatível, antes de qualquer coleta Windows ou criação de resultados. |
| Preparação repetível | `bash scripts/setup.sh` executado com sucesso antes e depois da publicação inicial; `restore --locked-mode`, build e suíte aprovados. Revisão também compilada com `-warnaserror`. |
| Publicação Windows x64 | `bash scripts/publish.sh` concluído. Interface, auxiliar e verificador são PE32+ x86-64; runtime .NET/WindowsDesktop e SQLite nativo incluídos. |
| Publicação PowerShell | `scripts/publish.ps1` executado por PowerShell 7.6.7 no Linux; restore travado, 103 testes e publicação dos três programas concluídos. Hashes e CRC do ZIP conferidos também por Python. |

Comandos reproduzíveis, na raiz do checkout:

```bash
bash scripts/setup.sh
source scripts/cloud-env.sh
dotnet test tests/PCAnalyzer.Tests/PCAnalyzer.Tests.csproj -c Release --no-build --no-restore \
  --logger 'trx;LogFileName=portable.trx' --results-directory artifacts/test-results
bash scripts/publish.sh
```

A sintaxe dos scripts PowerShell de publicação/empacotamento e do script fixo NetAdapter foi verificada pelo parser PowerShell 7.6.7. O cmdlet NetAdapter não foi executado no Linux. A publicação PowerShell foi executada nesta plataforma para produzir binários Windows; isso não executa a aplicação Windows. A primeira tentativa encontrou a restrição de escrita do mutex .NET em `/home/agent/.dotnet`; após permissão restrita a esse diretório, a publicação passou. O workflow de CI compila/testa Linux e Windows, realiza coleta passiva pelo verificador no runner Windows e conserva os resultados; sua execução/status não é prova do hardware do usuário. A consulta de status pela API GitHub retornou `Forbidden`; nenhum resultado remoto de CI é declarado aprovado aqui.

O ZIP é distribuição portátil sem assinatura/instalador. Build e pacote não comprovam inicialização ou precisão das APIs Windows.

## Correções identificadas na revisão

- CPU exige identificador de utilização válido, unidade correta e timestamps distintos; métricas de outra fonte não dão uma conclusão de ausência de anomalia.
- Pressão de memória exige disponível/commit/limite sincronizados na mesma janela e coexistência das condições. O Test Lab consulta disponibilidade física antes de alocar 32 MiB.
- Comparação tolera pequenas variações normais da cadência e sinaliza mudanças significativas. Diferenças de médias de contadores acumulados não são tratadas como melhorias.
- Operações SQLite, geração de relatórios e importação de CSV são retiradas da thread WPF. Artefatos grandes são serializados por streaming e publicados de forma atômica.
- Estatísticas evitam overflow para números finitos; FPS que não cabe em número finito fica indisponível.
- Scripts de publicação usam dependências travadas, verificam PE x64/runtimes/arquivos necessários e regeneram ZIP e hashes após mudanças.

Na implementação inicial, não foram validados: execução visual WPF, descoberta do computador informado, sensores NVIDIA reais, contadores localizados, capture/cleanup ETW, PresentMon/Vanguard, UAC, aplicação/restauração de energia/rede, overhead em jogos, comparação com instrumentos de referência ou compatibilidade operacional no Windows 10 Pro 22H2. As verificações de interface posteriores estão descritas acima; o restante segue o roteiro [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md) e [verify.md](verify.md).

A entrega inicial foi criada em um checkout sem commits. A revisão é preparada para envio à branch `main`; o SHA enviado é confirmado pelo Git no relatório final da tarefa. Os arquivos grandes em `artifacts/` ficam fora do commit e são reproduzíveis pelos scripts. A configuração cloud é um rascunho; publicação do ambiente e restauração numa nova tarefa não foram validadas aqui.
