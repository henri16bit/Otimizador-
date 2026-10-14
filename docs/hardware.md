# Hardware e telemetria passiva

`WindowsHardwareDiscovery : IHardwareDiscovery` e `WindowsTelemetryCollector : ITelemetryCollector` não produzem carga de benchmark e não instalam drivers. Todas as APIs lentas são chamadas em tarefa de trabalho, fora da thread WPF. Não há valores de exemplo na aplicação.

## Origem e limites

| Leitura | Implementação | Limitação importante |
| --- | --- | --- |
| Windows, CPU, BIOS, placa e módulos RAM | WMI `Win32_*`, dados SMBIOS | Campos podem faltar ou ser incorretos no firmware; não são sensores elétricos. |
| Configuração de RAM | `Speed` e `ConfiguredClockSpeed` preservados separadamente | `ConfiguredClockSpeed` documenta MHz; `Speed` herda unidade ns no esquema CIM, embora SMBIOS DDR moderno costume informar MT/s. Preservar campos brutos; zero é desconhecido. Não converter para clock elétrico nem tratar como SPD/XMP medido. Dois módulos não comprovam dual-channel. |
| GPU / driver | `Win32_VideoController`, NVML quando disponível | `AdapterRAM` de 32 bits é deliberadamente omitido como capacidade confiável; NVML obtém VRAM quando suportada. |
| Discos / partições / volumes / pagefile / Windows | `Win32_DiskDrive`, `Win32_DiskPartition`, `Win32_LogicalDiskToPartition`, `Win32_LogicalDisk`, `Win32_PageFileUsage` | Associação por referências WMI e `DiskIndex`; não inferir NVMe de `InterfaceType=SCSI`. |
| Interface real do disco | `MSFT_PhysicalDisk`, namespace Storage | `BusType=17` identifica NVMe; pode faltar no provedor. O ID dessa API é mantido separado do índice Win32 e não se presume equivalência. |
| Monitor / desktop | EDID `WmiMonitorID`, `EnumDisplayDevicesW`, `EnumDisplaySettingsW` | Refresh de desktop inteiro, incluindo modo principal e modos suportados na resolução atual; não mede FPS, VRR ou clique-para-fóton. |
| Inicialização e energia | `GetFirmwareType`, `PowerGetActiveScheme` | Leitura do tipo de firmware e GUID do plano, sem alterações. |
| CPU total | Delta de `GetSystemTimes` | Kernel inclui idle, que é subtraído. Com mais de 64 lógicos, usa `_Total` de Processor Information para abranger grupos. |
| Cada processador lógico, DPC agregado, interrupções | Performance Counters Processor Information | IDs do aplicativo seguem instâncias ordenadas por grupo,lógico; a instância original fica na origem. DPC em % não substitui eventos ETW. |
| CPU por processo | Delta de `Process.TotalProcessorTime` | Identidade é `(PID, data de criação)`, impedindo mistura por reutilização de PID. Normalizado por lógicos ativos do sistema. Processos protegidos/encerrados podem faltar. |
| RAM disponível, física, commit e limite | `GetPerformanceInfo` | Páginas × tamanho da página; commit é compromisso virtual, não RAM residente. |
| Paginação | Performance Counters Memory | Page Reads incluem arquivos mapeados; não provam pressão de pagefile isoladamente. |
| I/O de cada disco | Performance Counters PhysicalDisk | Índice numérico da instância corresponde ao índice de disco do Windows. Latência é média dos I/Os concluídos; sem I/O (ou sem taxa legível), latência fica nula/`InsufficientData`, nunca zero como medição real. |
| Rede passiva | `NetworkInterface.GetIPv4Statistics` e `Speed` | Delta de bytes/tempo; link não é throughput nem RTT. Nenhum ping automático. |
| GPU NVIDIA | DLL NVML já instalada pelo driver oficial | Caminhos absolutos em System32 / Program Files\NVIDIA Corporation\NVSMI; não carrega DLL de diretório de trabalho. Sensores ausentes viram estado explícito. |

Temperatura CPU, SPD direto, timings, XMP, canal efetivo, SMART/desgaste/temperatura NVMe e driver responsável por DPC não estão implementados neste coletor. A ausência aparece em capacidades/fatos. A localização do jogo depende da seleção do executável, sem varredura de pastas pessoais. Capacidade de VRAM sem NVML permanece indisponível.

## Contrato de coleta

`CollectAsync(interval, token)` lê imediatamente; o chamador controla a cadência. `interval` precisa estar entre 100 ms e 5 min (mínimo recomendado 1 s). As taxas registram tempo efetivamente transcorrido pelo relógio monotônico, não o intervalo solicitado. Timestamp é UTC. A primeira taxa é `Collecting`; memória e sensores instantâneos já podem estar disponíveis. Falhas são isoladas por métrica ou provedor.

Chaves importantes: `cpu.total.percent`, `cpu.logical.{n}.percent`, `cpu.queue.length`, `cpu.context_switches.per_second`, `memory.total.bytes`, `memory.available.bytes`, `memory.commit.bytes`, `memory.commit.limit.bytes`, `disk.{index}.read.bytes_per_second`, `disk.{index}.write.bytes_per_second`, `disk.{index}.read.latency.ms`, `disk.{index}.queue.length`, `gpu.{index}.utilization.percent`, `gpu.{index}.memory.used.bytes`, `gpu.{index}.temperature.celsius`, `network.{adapterId}.receive.bytes_per_second`, `collector.duration.ms`, `collector.cpu.percent` e `collector.working_set.bytes`.

NVML utilização usa a janela interna do driver (aproximadamente 1/6–1 s conforme o produto), cuja duração exata não é conhecida; `SamplingInterval=0` e a limitação explicita isso. Potência de Ampere (exceto GA100) ou mais recente é média interna de 1 s; em GA100/mais antigas é instantânea. Arquitetura e capacidade CUDA são consultadas para classificar a agregação; quando não identificáveis, potência fica indisponível para evitar metadados incorretos. Clock e P-state não são prova isolada de throttling. Flags de limitação incluem ociosidade e estados normais. Nenhum clock, potência ou limite é alterado.

Os coletores retêm apenas o último estado por processo/adaptador: até 4096 processos lidos e até 256 processos retornados (maior CPU/working set, incluindo o próprio aplicativo), 128 adaptadores/discos, 1024 instâncias lógicas e 32 GPUs. Inventário limita 4096 linhas por consulta. A UI e a persistência controlam o histórico. `DisposeAsync`/`Dispose` libera counters e NVML. Contadores são abertos uma vez; hardware conectado depois requer reiniciar a coleta para reenumerar counters. Rede e processos são reenumerados em cada lote.

WMI tem timeout de 10 s por consulta e cancelamento nas fronteiras; uma chamada nativa WMI em andamento nem sempre permite interrupção imediata. Nomes localizados de Performance Counters são resolvidos por IDs Perflib do inglês para idioma atual, sem modificar o Registro. Se o provedor estiver desabilitado, o contador fica indisponível; não há reparo automático de contadores.

## Validação

No Linux, build e testes validam compilação e a resposta explícita `Unsupported` com valores nulos, cancelamento e contrato de descarte. Não validam WMI, P/Invoke, NVML, drivers ou precisão de leituras reais.

No Windows 10 Pro 22H2 x64, conferir: inventário com Gerenciador de Dispositivos / Informações do Sistema; frequência de desktop com Configurações de Vídeo; memória commit com Gerenciador de Tarefas; CPU/counters e discos com PerfMon; VRAM/temperatura com `nvidia-smi` no mesmo período. Realizar observação ociosa e sob uma carga iniciada manualmente, comparar médias nas mesmas janelas, conferir volume do Windows e pagefile por associação, reutilização de PID e perda de sensores sem queda da UI. Registrar overhead em `collector.*`; nenhum limiar de custo universal é assumido. Medições no computador alvo ainda exigem essa validação.

Referências oficiais:

- [Win32_PhysicalMemory](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory)
- [Win32_LogicalDiskToPartition](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-logicaldisktopartition)
- [MSFT_PhysicalDisk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-physicaldisk)
- [GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)
- [GetPerformanceInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getperformanceinfo)
- [EnumDisplaySettingsW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsw)
- [NVIDIA NVML API](https://docs.nvidia.com/deploy/nvml-api/latest/)

Pacotes Microsoft `System.Management` e `System.Diagnostics.PerformanceCounter` 10.0.13 (MIT), com target .NET 10. A DLL NVML é fornecida pelo driver NVIDIA e não é redistribuída pelo projeto.
