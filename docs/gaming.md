# Gaming e PresentMon

O módulo importa CSV real do PresentMon e pode executar uma captura manual no Windows usando um executável `.exe` escolhido pelo usuário. Não baixa binários, não injeta código no jogo, não eleva privilégios automaticamente e não modifica Vanguard ou outro anticheat.

## Dados e unidades

`PresentMonCsvParser.ImportFileAsync(path, options)` e `ParseAsync(TextReader, options)` leem registros progressivamente, sem carregar o arquivo inteiro. O limite padrão é 1.000.000 de frames e 256 streams. Ao atingir um limite, `Complete` fica falso e as estatísticas descrevem somente a parte retida. O parser suporta aspas, vírgulas em campos e aspas escapadas; usa cultura invariável e rejeita timestamps não finitos/negativos.

Esquemas suportados:

| Esquema | Duração da aplicação | Timestamp |
| --- | --- | --- |
| PresentMon 1.x | `msBetweenPresents`, ms | `TimeInSeconds`, segundos convertidos para ms |
| PresentMon 2.x | `FrameTime`, ms; alternativa `CPUBusy + CPUWait`, ms | `CPUStartTime`, ms |
| Atual 3.x | `MsBetweenAppStart`, ms | `CPUStartTimeInMs`, ms; `CPUStartTimeInSeconds`, segundos |
| QPC | Mesmas durações, ms | `CPUStartQPC` / `TimeInQPC`: exige `QpcFrequencyHz` real; `CPUStartQPCTime[InMs]` já contém ms |
| Data/hora | Mesmas durações, ms | `CPUStartDateTime` / `TimeInDateTime`: exige `DateTimeUtcOffset` da captura |

Timestamps relativos não recebem uma época civil inventada. A importação de data/hora local exige um offset explícito; subsegundos de nove dígitos são limitados à precisão de ticks do .NET e a saída em ms. Capturas atravessando mudanças de horário civil devem usar timestamps relativos ou QPC.

O campo antigo `QPCTime` de algumas versões 1.x é ambíguo: o mesmo nome pode conter ticks ou uma conversão dependente das opções de exportação. Ele não é aceito como timestamp pelo parser; exporte novamente com `TimeInSeconds` ou um esquema com unidade explícita. Nenhuma unidade é inferida pela magnitude dos números. A leitura usa um buffer fixo de 4 KiB e limita o tamanho do registro antes de alocar uma linha inteira, inclusive para campos com múltiplas linhas.

Identidade de stream inclui aplicação, PID, endereço de swap chain e, quando presente, tipo de frame. Um timestamp que retrocede inicia outro segmento. Nenhum FPS é calculado agregando processos, swap chains ou frames gerados com frames da aplicação. CSV não informa necessariamente a criação do processo: a reutilização de um PID com a mesma identidade e timestamp monotônico não pode ser identificada com certeza; prefira capturas individuais.

`Dropped` de 1.x é preservado. Em 2.x, `DisplayedTime = NA` indica ausência de exibição; estados ausentes permanecem desconhecidos. Cadência da aplicação usa todos os intervalos positivos válidos, incluindo presents descartados. Cadência exibida é separada e usa `DisplayedTime` ou `msBetweenDisplayChange`, somente quando conhecido. Durações zero (inclusive o primeiro intervalo sem precedente), ausentes, negativas ou não finitas ficam fora dos cálculos. Os frames são preservados para contar drops e expor limitações. Campos opcionalmente indisponíveis permanecem nulos.

## Estatísticas

`FrameStatisticsCalculator.Calculate(durationsMilliseconds, observedSeconds)` calcula:

- FPS médio = `1000 / média da duração`, não média do inverso dos intervalos.
- p95, p99 e p99,9 de frametime: interpolação R-7, com pelo menos 20, 100 e 1.000 intervalos respectivamente.
- 1% low e 0,1% low = `1000 / média dos ceil(N × fração) intervalos mais lentos`. Exigem 1.000 e 10.000 intervalos respectivamente, garantindo pelo menos dez intervalos no extremo.

Menos de 30 intervalos ou menos de um segundo observado produz `InsufficientData`, mesmo quando uma média descritiva pode ser calculada. O limite mínimo de percentis não implica estabilidade estatística. Repita um cenário controlado por pelo menos 30 segundos, mantenha as mesmas condições e compare repetições. ETW não prova sozinho um gargalo, não mede ping e não informa o tempo físico de resposta do monitor.

## Captura manual

`PresentMonCaptureService.InspectAsync(executablePath)` executa apenas `--help` e verifica o identificador/versão do PresentMon Console e as opções necessárias. Ajuda com `--` seleciona CLI moderna, ajuda com `-` seleciona CLI legada. CLI desconhecida é recusada; não há tentativa com argumentos presumidos. Use um executável obtido de uma fonte confiável: a inspeção de ajuda verifica compatibilidade, não assinatura ou procedência.

`CaptureAsync(new PresentMonCaptureRequest(exePath, pid, duration, csvPath))` exige Windows, PID em execução, duração de 1–600 segundos e um caminho absoluto `.csv` novo em pasta existente. O serviço usa sessão ETW exclusiva, argumentos separados sem shell, captura limitada ao PID, tempo limitado e preserva dropped frames. Após saída, cancelamento ou falha, verifica somente seu nome de sessão gerado aleatoriamente. Se ainda estiver ativo, executa `terminate_existing_session` com esse nome exato. A ausência da sessão é verificada por consulta somente de leitura via TraceEvent. Falha de limpeza informa o nome e comando de recuperação. Nunca interrompe sessões de outras aplicações. Um CSV parcial após cancelamento/falha é preservado.

ETW pode exigir permissões administrativas. Falta de permissão é reportada como `PermissionRequired`; o módulo não contorna restrições do Windows ou anticheat. Versões do executável ou políticas de jogos podem impedir coleta: nesses casos use um CSV produzido por uma execução permitida. Validação do executável real e captura durante Valorant exigem uma máquina Windows e teste manual autorizado. Nesta implementação, o build e os testes de importação/estatística são independentes de Windows.

Fontes oficiais consultadas para unidades/esquemas e CLI:

- [PresentMon Console, atual](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md)
- [PresentMon 2.3.1 CsvOutput.cpp](https://github.com/GameTechDev/PresentMon/blob/v2.3.1/PresentMon/CsvOutput.cpp)
- [PresentMon 2.3.1 CommandLine.cpp](https://github.com/GameTechDev/PresentMon/blob/v2.3.1/PresentMon/CommandLine.cpp)
- [PresentMon 1.9.2](https://github.com/GameTechDev/PresentMon/blob/v1.9.2/README.md#csv-columns)
