# Auditoria de dispositivos, IRQ, afinidade e USB

Este módulo registra três evidências separadas: configuração persistente, capacidade reportada pelo dispositivo e recursos atualmente atribuídos pelo Windows. Uma máscara de Registro ou `MSISupported = 1` não comprova afinidade efetiva, modo MSI/MSI-X em uso ou redução de latência.

## Origens de leitura

| Informação | Origem | Interpretação permitida |
| --- | --- | --- |
| Dispositivos presentes | SetupAPI `DIGCF_PRESENT` e `DIGCF_ALLCLASSES` | Nome, instance/hardware IDs, classe, serviço, barramento, localização e pais disponíveis na captura. |
| Driver | `SPDRP_DRIVER` e leitura da chave de classe correspondente | Versão, provedor, descrição e INF, quando expostos. Não certifica que seja o melhor driver. |
| Estado PnP | Configuration Manager `CM_Get_DevNode_Status` | Código de problema reportado pelo Windows. Código 0 não certifica desempenho, latência ou ausência de defeito. |
| Capacidade PCI | `DEVPKEY_PciDevice_InterruptSupport` e `InterruptMessageMaximum` | Capacidade line-based/MSI/MSI-X e máximo de mensagens do hardware. Não é o modo nem a quantidade atualmente utilizada. |
| Configuração de interrupções | Registro do dispositivo enumerado | `MSISupported`, `MessageNumberLimit`, `DevicePolicy` e `AssignmentSetOverride`, com valor bruto, tipo e estado da leitura. |
| Recursos IRQ atribuídos | `CM_Get_First_Log_Conf(ALLOC_LOG_CONF)` e descritores `IRQ_DES_64` | IRQ, grupo, máscara e flags atribuídos nesta configuração. Não mede eventos nem usa os requisitos ou configuração de boot como substituto. |
| Núcleos e SMT | `GetLogicalProcessorInformationEx(RelationProcessorCore)` | Núcleos físicos, processadores lógicos, grupos, eficiência e pares SMT realmente informados. |
| Relações USB/HID | `CM_Get_Parent` e identidades PnP | Árvore lógica de pais e controladores, inclusive quando parcial. |

São enumerados dispositivos PCI, USB, HID, HDAUDIO e classes relevantes de vídeo, rede, armazenamento, áudio, mouse e teclado. A enumeração é limitada a 4.096 dispositivos presentes; cada dispositivo tem limite de 1.024 descritores IRQ. Identificadores de chave de classe são validados antes da leitura. Não há instalador de driver, setters SetupAPI/Configuration Manager nem abertura de chave com permissão de escrita.

## Afinidade e grupos

`AssignmentSetOverride` aceita os tipos documentados `REG_BINARY`, `REG_DWORD` ou `REG_QWORD`, com largura compatível com a plataforma. Bytes binários são interpretados em little-endian; o bit 63 é preservado em x64. Texto contendo “0x…” não é tratado como máscara numérica válida.

`DevicePolicy = 4` é a política que seleciona processadores por essa máscara. Sua presença não confirma aplicação. Um valor ausente, um tipo incompatível e acesso negado são estados diferentes. Políticas fora do contrato público e máscaras sem processadores ou incompatíveis com a topologia são apresentadas com a evidência correspondente, sem declarar uma causa de input lag.

O contexto de grupo de uma máscara persistente só é associado automaticamente quando a topologia contém um único grupo. Em vários grupos, o Registro consultado não fornece sozinho o grupo: o resultado fica indeterminado. Descritores IRQ alocados possuem campo de grupo próprio, preservado separadamente das flags.

Pares SMT são definidos pela máscara do núcleo, nunca pela suposição de que processadores consecutivos formam um par. Compartilhar uma CPU lógica entre dois dispositivos não gera diagnóstico de conflito. Não existe distribuição de núcleos recomendada universalmente.

## USB e medições ainda necessárias

A árvore USB/HID preserva os IDs reais dos pais e limita profundidade/ciclos. Ancestral fora da seleção enumerada é identificado como árvore parcial. Essa árvore não mede polling, intervalos de eventos de entrada, velocidade negociada, suspensão efetiva, número físico da porta ou input lag. O aplicativo não instala filtros de entrada.

A atividade efetiva de interrupções exige captura ETW explícita, sob uma carga identificada. O módulo ETW existente mede DPC/ISR e suas limitações de captura; esta auditoria passiva não correlaciona automaticamente cada recurso IRQ/PnP com eventos de uma thread de jogo. Modo MSI/MSI-X efetivo e distribuição real de eventos não são inferidos a partir de flags de disparo, número do IRQ ou capacidade PCI.

## ABI e validação

Os testes `DeviceAuditTests` exercitam registros nativos de topologia x86/x64, múltiplos grupos, pares SMT não consecutivos, bit 63, buffers truncados, núcleos duplicados, tipo/largura de máscara, valores ausentes/permissão/tipo, política 4, mapeamento de recursos e árvore USB parcial/cíclica. O decodificador `IRQ_DES_64` preserva os campos `USHORT Flags`, `USHORT Group`, `ULONG Alloc_Num` e `ULONG64 Affinity`; rejeita ranges truncados ou com tamanho inesperado.

```bash
dotnet test tests/PCAnalyzer.Tests/PCAnalyzer.Tests.csproj -c Release --filter FullyQualifiedName~DeviceAuditTests
```

Esses casos verificam decodificação e interpretação com entradas controladas. A verificação real do Windows também executa o provedor passivo, preserva o resultado no SQLite e confere a exportação. Isso não substitui validação em uma máquina com vários grupos, diferentes controladores USB/NVMe ou na configuração física do usuário. Uma execução WOW64 em Windows x64 não publica topologia de afinidade potencialmente truncada.

## Referências técnicas

- [Microsoft: afinidade e prioridade de interrupções](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/interrupt-affinity-and-priority).
- [Microsoft: habilitação MSI no Registro](https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/enabling-message-signaled-interrupts-in-the-registry).
- [Microsoft: `GetLogicalProcessorInformationEx`](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getlogicalprocessorinformationex) e [relação de processador](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-processor_relationship).
- [Microsoft: `IRQ_DES_64`](https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ns-cfgmgr32-irq_des_64) e [ConfigMgr SDK](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/cfgmgr32.h).
- [Microsoft: propriedades PCI do SDK](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/pciprop.h): IDs 14/15; máscara de capacidade line-based=1, MSI=2, MSI-X=4.
- [Microsoft: USBView](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/usb-device-tree-viewer), como referência para a investigação de uma árvore USB, sem dependência do aplicativo.

As APIs são de modo de usuário e a leitura implementada tem Windows 10/11 x64 como alvo. A existência de uma API no sistema não garante que todo dispositivo/driver exponha todas as propriedades.
