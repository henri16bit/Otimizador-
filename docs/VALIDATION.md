# Validação executada — 14/10/2026

Ambiente: Debian 13 x64, SDK .NET 10.0.402 (runtime 10.0.13). O SDK foi baixado de `builds.dotnet.microsoft.com` e verificado por SHA-512 contra o manifesto oficial. Pacotes NuGet restaurados com verificação normal de TLS/assinaturas; versões e hashes estão nos lockfiles.

## Revisão da interface

A suíte portátil passou a **116 casos aprovados**, incluindo apresentação de unidades sem modificar as amostras, RAM física sincronizada, leituras indisponíveis, precisão de valores pequenos, escala dos gráficos e recebimento identificado de um único adaptador. Os cinco testes Python de pacote e os cinco cenários PowerShell isolados de publicação também passaram. O resultado C# está em `artifacts/test-results/interface.trx`.

O workflow de release verifica no Windows as oito páginas, sete abas internas, atalhos, tamanho compacto, abertura e fechamento, e guarda capturas PNG em seus artefatos. O resultado da execução é registrado em `refs/notes/pcanalyzer-releases`; uma captura da visão geral pode ser obtida por `refs/notes/pcanalyzer-ui-preview`. A API GitHub continua inacessível neste ambiente, mas o resultado remoto pode ser conferido pelo Git.

A versão anterior `v0.1.0-preview.4` já teve abertura/renderização/fechamento confirmados no runner Windows, com [registro da execução](https://github.com/henri16bit/Otimizador-/actions/runs/38617713687) e ZIP/SHA256 publicados. Isso não valida sensores, alterações administrativas, ETW/PresentMon ou o Windows 10 Pro 22H2 do computador alvo.

## Validação da implementação inicial

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

Não foram validados neste ambiente: execução visual WPF, descoberta do computador informado, sensores NVIDIA reais, contadores localizados, capture/cleanup ETW, PresentMon/Vanguard, UAC, aplicação/restauração de energia/rede, overhead em jogos, comparação com instrumentos de referência ou compatibilidade operacional no Windows 10 Pro 22H2. Siga [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md) e [verify.md](verify.md).

A entrega inicial foi criada em um checkout sem commits. A revisão é preparada para envio à branch `main`; o SHA enviado é confirmado pelo Git no relatório final da tarefa. Os arquivos grandes em `artifacts/` ficam fora do commit e são reproduzíveis pelos scripts. A configuração cloud é um rascunho; publicação do ambiente e restauração numa nova tarefa não foram validadas aqui.
