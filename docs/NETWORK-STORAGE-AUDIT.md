# Auditoria de rede, memória e armazenamento

Os provedores `NetworkConfigurationAuditProvider` e `StorageMemoryAuditProvider` adicionam snapshots de configuração ao relatório de consultoria. Eles não aplicam ajustes, não iniciam ICMP ou benchmarks e não escrevem em discos físicos. Testes de carga e capturas existentes continuam separados e exigem ação explícita.

## Rede: correção da identidade e coleta parcial

A consulta usa o `InterfaceGuid` validado, normalizando GUIDs com ou sem chaves e diferenças de maiúsculas. Se `Get-NetAdapter` não retornar esse GUID, o fallback exige uma única entrada `Win32_NetworkAdapter` com o GUID exato, um índice de interface correspondente e a mesma identidade PnP. Um GUID válido contraditório, múltiplos candidatos ou nome de interface duplicado interrompem a consulta; o código não escolhe outro adaptador pelo nome comercial Intel.

Depois da associação, o nome é escapado como padrão literal para os cmdlets. Isso evita que caracteres `[]*?` no nome selecionem outra interface. Cada resposta JSON preserva a identidade solicitada e o parser a verifica novamente. A auditoria inclui até oito interfaces de tipos Ethernet/Wi-Fi, priorizando as ativas; a Intel I219-V não é codificada como dispositivo fixo. Interfaces virtuais que se apresentam como Ethernet podem aparecer nos detalhes, com `HardwareInterface` quando fornecido. Os resumos não utilizam GUID como nome da conexão.

As leituras de propriedades, RSS, energia e contadores são independentes. Uma falha em propriedades avançadas não apaga identidade e driver já obtidos. Valores nulos e funcionalidades não suportadas continuam indisponíveis; zero válido de contador permanece zero. Falhas têm estado explícito e mensagem curta, com detalhes em `%LOCALAPPDATA%\PCAnalyzer\logs\network.log`. CLIXML é decodificado com DTD desabilitado e nunca exposto como XML na mensagem normal.

| Fonte | Valores lidos | Limite de interpretação |
| --- | --- | --- |
| `NetworkInterface` / `Get-NetAdapter` | Nome, descrição, estado, identidade, velocidade do enlace, versão/data/informação do driver | Identificação não prova consulta avançada funcional, throughput ou qualidade da conexão. |
| `Get-NetAdapterAdvancedProperty -AllProperties` | RegistryKeyword, valor bruto, rótulo atual e enumerações anunciadas pelo driver | Não inventa EEE, offload, RSS ou moderação quando o driver não os expõe. Configuração diferente não é automaticamente pior. |
| `Get-NetAdapterRss` | Enabled, Profile, NumberOfReceiveQueues, Base/MaxProcessorGroup/Number, MaxProcessors, RssProcessorArray | Estado configurado não mede interrupções por CPU nem demonstra que todos os fluxos usam todas as filas. |
| `Get-NetAdapterPowerManagement` | AllowComputerToTurnOffDevice, DeviceSleepOnDisconnect, SelectiveSuspend, WakeOnMagicPacket/Pattern, ArpOffload, NSOffload, quando disponíveis | Não confirma transições de energia durante uma partida. |
| `Get-NetAdapterStatistics` | ReceivedBytes, SentBytes, ReceivedPacketErrors, OutboundPacketErrors, ReceivedDiscardedPackets, OutboundDiscardedPackets | Contadores acumulados sem diferença temporal não medem perda atual nem pacotes do jogo. |
| `Get-NetTCPSetting` | SettingName, AutoTuningLevelLocal/GroupPolicy, CongestionProvider, EcnCapability, Timestamps, ScalingHeuristics, MinRtoMs, InitialRtoMs, DelayedAckTimeoutMs/Frequency | Perfis disponíveis não identificam a política de uma conexão. Configurações TCP não são aplicadas ao UDP do jogo. |

Cada processo PowerShell tem prazo de 30 segundos e saída limitada. O coordenador de auditoria também impõe prazo por provedor e não enfileira uma segunda consulta nativa enquanto a anterior está pendente. Muitas interfaces ou um driver lento podem reduzir cobertura nessa sessão; isso permanece explícito no relatório. Não há elevação automática.

Fontes oficiais: [Get-NetAdapter](https://learn.microsoft.com/powershell/module/netadapter/get-netadapter), [Get-NetAdapterAdvancedProperty](https://learn.microsoft.com/powershell/module/netadapter/get-netadapteradvancedproperty), [Get-NetAdapterRss](https://learn.microsoft.com/powershell/module/netadapter/get-netadapterrss), [Get-NetAdapterPowerManagement](https://learn.microsoft.com/powershell/module/netadapter/get-netadapterpowermanagement), [Get-NetAdapterStatistics](https://learn.microsoft.com/powershell/module/netadapter/get-netadapterstatistics), [Get-NetTCPSetting](https://learn.microsoft.com/powershell/module/nettcpip/get-nettcpsetting). Os contratos dos cmdlets não garantem que uma versão específica do driver Intel exponha todas as propriedades.

## Memória: configuração informada pelo firmware

`Win32_PhysicalMemory` fornece Manufacturer, PartNumber, Capacity, DeviceLocator, BankLabel, ConfiguredClockSpeed, Speed, SMBIOSMemoryType, DataWidth e TotalWidth por módulo. O relatório preserva os valores brutos. Para tipos DDR reconhecidos, a velocidade configurada é mostrada como taxa de transferência informada pelo SMBIOS, sem multiplicar por dois.

A [documentação WMI](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory) mantém a unidade MHz na projeção; a estrutura Type 17 da [especificação SMBIOS, DSP0134](https://www.dmtf.org/standards/smbios) define a velocidade da memória em MT/s. Por isso o relatório descreve a origem e não apresenta esse campo como clock elétrico medido. Tipo desconhecido ou velocidade zero não produz taxa inferida. `Speed` não comprova frequência nominal anunciada no kit.

Não há leitura direta de SPD, XMP, timings ou canais ativos. Dois módulos ou BankLabel não comprovam dual channel; uma taxa inferior à anunciada não é classificada automaticamente como defeito. O benchmark manual de acesso à memória é outra operação e não é iniciado pelo snapshot.

## Armazenamento: identidade por unidade

| Fonte | Valores implementados | Critério de associação e limites |
| --- | --- | --- |
| `MSFT_PhysicalDisk` | DeviceId, UniqueId, modelo/nome, FirmwareVersion, BusType, MediaType, Size, HealthStatus, OperationalStatus, PhysicalLocation, IsWriteCacheEnabled, IsPowerProtected | Identidade própria por disco, sem assumir ordem ou modelo Kingston/XPG. NVMe é identificado por BusType 17. Controlador e driver não são associados por essa leitura. |
| `MSFT_StorageReliabilityCounter` | Temperature/TemperatureMax, Wear, PowerOnHours, erros de leitura/escrita totais, corrigidos e não corrigidos, Read/Write/FlushLatencyMax | Exige associação única `MSFT_PhysicalDiskToStorageReliabilityCounter`, caminho WMI único e DeviceId correspondente. Ausência ou ambiguidade não reutiliza valores de outro disco. |
| `MSFT_Disk` + `MSFT_Partition` | Número de disco e caminhos dos volumes | UniqueId físico/lógico correspondente e único. Sem associação confiável, Windows e paginação ficam não identificados. |
| `Win32_OperatingSystem` + `Win32_PageFileUsage` | SystemDrive e caminhos de paginação correspondentes aos volumes | Disponibilidade das consultas registrada separadamente. Nenhum pagefile identificado não prova paginação desativada. |

`HealthStatus=0` significa somente que o provedor reporta Healthy; `1/2` produz indicação de investigação, sem comprovar falha física ou causa de stuttering. Outros códigos não viram saúde adequada. Temperatura zero fica indisponível; zero de contador de erro continua válido. Wear é desgaste consumido reportado pelo provedor, não porcentagem de vida restante. Latências máximas são em ms; máximas e totais pertencem ao período do provedor, sem atribuição a uma partida. Não há reset de contadores.

Fontes oficiais: [MSFT_PhysicalDisk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-physicaldisk), [MSFT_StorageReliabilityCounter](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagereliabilitycounter), [MSFT_Disk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk), [MSFT_Partition](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition), [Win32_PageFileUsage](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-pagefileusage).

Permanecem pendentes: SMART/NVMe bruto, mapeamento controlador/driver por disco, filas e throughput de cada NVMe em janela de jogo, correlação de I/O com frametime e localização do jogo a partir de executável selecionado. As políticas gerais de PCIe e energia são consultadas pelo provedor Windows; não comprovam o estado físico de energia de cada SSD.

## Validação reproduzível

- `NetworkAuditTests`: identidade divergente, falhas parciais, enumerações de driver, valores nulos/zero e CLIXML malformado/DTD/escape único.
- `StorageAuditTests`: taxa DDR sem SPD/canais fictícios, duas identidades de disco, associação exata de confiabilidade, duplicidades, temperatura zero, volumes e falhas de consultas auxiliares.
- `tests/network_tests/NetworkRead.Tests.ps1`: executa o script de produção com cmdlets de leitura substituídos por fixtures. Verifica GUID normalizado, fallback restrito, identidades contraditórias/ambíguas, nome com curingas, falha parcial e TCP. Não acessa drivers nem modifica configurações; requer PowerShell 7 para o executor de testes, sem ser dependência instalada pelo aplicativo. A execução de produção usa o Windows PowerShell presente no Windows.

Esses testes comprovam regras, associação e tratamento de respostas controladas. A execução real da Intel I219-V e dos dois NVMe no Windows 10 do usuário ainda precisa registrar valores e estados: comparar propriedades anunciadas com o driver, firmware/modelo com Gerenciador de Dispositivos, volumes com Gerenciamento de Disco e contadores com o provedor Storage. Uma consulta indisponível deve ser registrada como limitação. O resultado do runner Windows, quando disponível, é evidência desse runner, não do computador do usuário; consultar `VALIDATION.md` para a execução efetivamente realizada.
