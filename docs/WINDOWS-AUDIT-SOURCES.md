# Auditoria de configurações do Windows

A implementação está em `WindowsConfigurationAuditProvider`, com catálogo extensível em `WindowsAuditCatalog` e interpretação em `PerformanceAuditEngine`. Cada observação preserva origem, escopo, tipo, valor bruto, disponibilidade e limitações. A leitura do Registro utiliza `OpenSubKey(..., writable: false)`; a rotina não grava políticas, não solicita elevação automaticamente e não inicia benchmarks.

## Leituras implementadas

| Área | Origem e valores lidos | O que a leitura permite concluir |
| --- | --- | --- |
| Windows | `CurrentVersion`: `ProductName`, `CurrentBuildNumber`, `DisplayVersion`, `UBR`, nas visões 32 e 64 bits | Contexto de versão; `ProductName` isolado não confirma edição, compatibilidade ou identidade comercial atual. |
| Agendamento | `PriorityControl\Win32PrioritySeparation` | DWORD persistente em hexadecimal e decimal, sem afirmar superioridade de `0x26` sobre `0x2A`. Não determina prioridade de threads. |
| MMCSS | `SystemProfile`: `SystemResponsiveness`, `NetworkThrottlingIndex`; subchaves `Tasks`: `Priority`, `GPU Priority`, `SFIO Priority`, `Clock Rate`, `Affinity`, `Scheduling Category`, `Background Only`, `BackgroundPriority` | Interpretação do contrato publicado para os campos documentados. A existência de Games não prova que um jogo registrou suas threads nessa tarefa. |
| IFEO | Subchaves `Image File Execution Options\<executável>\PerfOptions`: `CpuPriorityClass`, `IoPriority`, `PagePriority` | Inventário persistente associado pelo nome aos processos do snapshot. Não presume identidade por caminho ou atribui a classe atual ao IFEO. |
| Processos | PID, nome, `PriorityClass`, `ProcessorAffinity`, processo em primeiro plano | Classe e máscara observadas pela API. Leituras negadas permanecem indisponíveis. Não acessa memória do jogo. |
| Preferências gráficas | GameBar: `AutoGameModeEnabled`, `AllowAutoGameMode`; GraphicsDrivers: `HwSchMode`; GameConfigStore: quatro flags de tela cheia; AppCompatFlags/Layers; UserGpuPreferences | Preferências persistentes. A auditoria não confirma ativação efetiva do Game Mode/HAGS, GPU utilizada, modo de apresentação ou efeito sobre o jogo. |
| Outros tweaks gráficos/CPU | `Dwm\OverlayTestMode`; `PowerThrottling\PowerThrottlingOff` | Valor encontrado e contrato não confirmado para o contexto atual; não transforma DWORD em MPO ou Power Throttling efetivo. |
| Mouse e teclado | MouseSpeed, MouseThreshold1/2, MouseSensitivity, SmoothMouseX/YCurve, MouseDataQueueSize, KeyboardDataQueueSize | Configuração persistente; curvas preservadas como bytes. Não mede polling ou input lag. O efeito em Raw Input não é presumido. |
| Ponteiro atual | `SystemParametersInfoW`: `SPI_GETMOUSE`, `SPI_GETMOUSESPEED` | Limiares, aceleração e velocidade atuais do ponteiro do usuário; não demonstra efeito em jogo. |
| Memória virtual | `Memory Management`: PagingFiles, DisablePagingExecutive, LargeSystemCache, ClearPageFileAtShutdown | Valores persistentes separados de commit, pressão de memória e arquivos de paginação efetivamente identificados. |
| Energia | `PowerGetActiveScheme`, `PowerReadACValueIndex`, `PowerReadDCValueIndex` | Plano ativo e índices separados para tomada/bateria: CPU mínima/máxima, mínimo de núcleos sem parking, boost, preferência energia/desempenho, economia PCIe e suspensão seletiva USB. Não comprova aplicação física pelo firmware/driver. |
| Serviços | `Win32_Service`: MMCSS, SysMain, WSearch, Audiosrv, AudioEndpointBuilder, BITS, Spooler, DiagTrack | Estado, modo de inicialização e PID. Não presume carga ou recomenda desativar serviços por estarem ativos. Dependências não são coletadas. |
| Inicialização | HKLM/HKCU Run, RunOnce e StartupApproved/Run, Run32, StartupFolder | Entradas persistentes; não comprova execução. StartupApproved fica bruto, sem assumir formato binário estável. |
| Tarefas | Task Scheduler COM: nome/caminho, estado, Enabled, LastTaskResult, LastRunTime, NextRunTime | Snapshot de até 256 tarefas e 128 pastas, incluindo tarefas ocultas. Limites e falhas são registrados; não executa tarefas nem atribui consumo. |
| Temporizadores | `Stopwatch.Frequency`/QPC, `timeGetDevCaps` | Frequência do contador e limites de resolução multimídia. Não identifica HPET/TSC nem a resolução efetiva por processo. Não chama `timeBeginPeriod`. |
| BCD | `bcdedit /enum {current}` para useplatformclock, useplatformtick, disabledynamictick, tscsyncpolicy | Somente se o programa já estiver elevado; cinco segundos de prazo. Ausência não vira valor padrão presumido. A opção não confirma reinicialização, fonte QPC ou benefício. |

O catálogo consulta chaves determinadas e limita enumerações de Registro a 256 entradas por origem. Não varre indiscriminadamente todo o Registro. Fora do Windows retorna cobertura não suportada, sem inventar valores.

## Critérios documentados de interpretação

- `SystemResponsiveness`: múltiplos não exatos são arredondados para baixo em dezenas; valores abaixo de 10 ou acima de 100 são tratados como 20 pelo contrato. `100` desativa MMCSS. É uma garantia para tarefas de baixa prioridade, sem equivaler a CPU reservada para o jogo.
- MMCSS `Priority` e `BackgroundPriority` documentam 1–8. Para categoria `High`, `Priority` é tratado como 2; o código preserva o número original e não o converte em prioridade atual de thread.
- MMCSS `GPU Priority` é declarado não utilizado; `SFIO Priority` também. O relatório os identifica como campos sem efeito documentado nesse mecanismo.
- `Clock Rate` continua uma dica de granularidade; a garantia histórica de Vista/Server 2008 foi removida a partir do Windows 7. Não é interpretado como temporizador de entrada.
- MMCSS `Affinity` igual a 0 ou `0xFFFFFFFF` não impõe afinidade nessa tarefa. Isso não elimina restrições por processo, thread, CPU Sets ou interrupções.
- `NetworkThrottlingIndex`, IFEO, Game Mode/HAGS por Registro, MPO e outros contratos não confirmados mantêm classificação de efeito não verificado. A referência de um mecanismo relacionado não valida um tweak popular.
- Classe de processo `RealTime` produz indicação de investigação pelo risco documentado de impedir trabalho importante. `Normal` apenas significa que esse item não encontrou prioridade de tempo real; não certifica agendamento ou desempenho adequados.
- Configuração presente, valor reconhecido, aplicação ao processo, comportamento observado e benefício são níveis distintos. Um campo `effective` vindo de API não basta para classificar desempenho como adequado.

`REG_DWORD` e `REG_QWORD` são formatados como inteiros sem sinal. Por exemplo, `0x26 = 38`, `0x2A = 42`; `-1` armazenado em DWORD é preservado como `0xFFFFFFFF = 4294967295`. Strings não são convertidas silenciosamente em DWORD. Valores ausentes não viram zero, configuração ideal ou problema.

## Fontes e aplicabilidade

Referências técnicas revistas durante esta implementação; aplicabilidade alvo: Windows 10 x64, com limitações de contrato/driver descritas acima. Contratos disponíveis no Windows 11 não confirmam comportamento no Windows 10 do usuário.

| Fonte oficial | Uso na implementação |
| --- | --- |
| [Multimedia Class Scheduler Service](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service) | Campos, tipos, faixas, arredondamento, prioridade High e propriedades ignoradas. |
| [Scheduling Priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities) e [Priority Boosts](https://learn.microsoft.com/en-us/windows/win32/procthread/priority-boosts) | Classes e limites de interpretação do agendamento; não documentam todos os valores populares de IFEO. |
| [SystemParametersInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow) | Leitura dos parâmetros atuais do ponteiro. |
| [Keyboard and mouse class drivers](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/keyboard-and-mouse-class-drivers) | Contexto das filas; não comprova benefício de alterar capacidade nem polling. |
| [PowerReadACValueIndex](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadacvalueindex) e [PowerReadDCValueIndex](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreaddcvalueindex) | Leitura dos índices do plano; sem APIs de escrita. |
| [Win32_Service](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-service), [Run and RunOnce](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys), [RegisteredTask](https://learn.microsoft.com/en-us/windows/win32/taskschd/registeredtask) | Inventário de serviços, entradas e tarefas. |
| [Acquiring high-resolution time stamps](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps), [timeGetDevCaps](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timegetdevcaps), [timeBeginPeriod](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod) | Distinguir QPC, capacidade e resolução solicitada; mudança de semântica por processo desde Windows 10 2004. |
| [BCDEdit /set](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set) | Semântica e finalidade de depuração das opções. A implementação apenas enumera. |
| [Registry Redirector](https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-redirector) | Visões do Registro. Identidade do Windows é lida em ambas; demais chaves usam a visão 64 bits. |

Não há ferramenta externa incorporada nesse provedor. APIs nativas, Registro, WMI, Task Scheduler COM e o `bcdedit.exe` existente no sistema resolvem as leituras implementadas. Não há instalação automática.

## Validação e limites pendentes

`WindowsAuditTests` exercita valores ausentes, tipos errados, representação numérica, regras MMCSS, separação IFEO/processo, classes observadas e parser BCD. Esses testes usam observações controladas; não comprovam que o hardware do usuário foi consultado. `PCAnalyzer.Verify` registra a cobertura real e compara leituras quando executado no Windows; consultar a evidência da execução em `VALIDATION.md`.

Permanecem pendentes: adesão efetiva de threads ao MMCSS, prioridades por thread, CPU Sets, context switches por processo, políticas de Power Throttling efetivas por processo, estado real HAGS/MPO, chamadas de timer por processo, fonte física QPC, dependências e consumo por serviço/tarefa e cobertura IFEO 32 bits distinta. Esses itens não são apresentados como diagnóstico concluído.

Para validar no Windows 10 Pro 22H2 do usuário: comparar DWORD/tipo com Regedit sem editar; classe/afinidade com Gerenciador de Tarefas/Process Explorer; plano e índices com consulta do Windows; tarefas e serviços com seus consoles. Repetir como usuário comum e, somente por decisão explícita do usuário, em sessão elevada para BCD. Guardar relatório e log de falhas; não modificar configurações para fazer a leitura passar.
