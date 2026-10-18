# Auditoria NVIDIA: perfis e preferências do driver

A auditoria consulta o DRS do driver NVIDIA instalado e preserva o valor, a origem e o contexto de cada leitura. Ela não altera perfis, inicia jogos ou conclui que uma preferência trouxe ganho de desempenho.

## Implementação

`NvapiDrsReader` carrega somente `%SystemRoot%\System32\nvapi64.dll`, por caminho absoluto. A DLL pertence ao driver instalado e não é distribuída no ZIP do PCAnalyzer. Windows x64 é obrigatório para esse adaptador; ausência da DLL, API indisponível e permissão negada aparecem como estados de leitura distintos.

O adaptador resolve apenas `Initialize`, `Unload`, `DRS_CreateSession`, `DRS_DestroySession`, `DRS_LoadSettings`, `DRS_GetCurrentGlobalProfile`, `DRS_GetBaseProfile`, `DRS_GetProfileInfo`, `DRS_FindApplicationByName` e `DRS_GetSetting`. A sessão é uma cópia de leitura das configurações carregadas. Não existem entradas `SetSetting`, `SaveSettings`, criação de perfil ou exclusão de associação no adaptador.

As declarações e IDs foram conferidos nos arquivos públicos `nvapi.h`, `nvapi_interface.h` e `NvApiDriverSettings.h`, no commit [`70d337db9186e968eab622f7e786de7e437faf3d`](https://github.com/NVIDIA/nvapi/tree/70d337db9186e968eab622f7e786de7e437faf3d). Esse catálogo não garante que todo driver instalado implemente todos os IDs. Os avisos MIT desses cabeçalhos estão em [NVIDIA-NVAPI-LICENSE.txt](NVIDIA-NVAPI-LICENSE.txt).

## Perfis e herança

- Consulta o perfil global atual e o perfil base.
- Procura associações dos executáveis observados, preferindo o caminho completo do processo quando acessível. A consulta é limitada a 256 executáveis observados, com prioridade para VALORANT.
- Consulta `VALORANT-Win64-Shipping.exe` como candidato quando ele não foi observado. Encontrar esse perfil não comprova que o jogo esteja instalado ou em execução.
- Preserva o nome do perfil, PIDs observados, consulta por nome/caminho, associação predefinida e restrições de pasta, flags e linha de comando retornadas pelo DRS. Essas restrições adicionais não são validadas independentemente.
- Para cada opção, preserva `settingLocation`, `isCurrentPredefined`, `isPredefinedValid`, valor atual decimal/hexadecimal e valor predefinido quando válido. Um valor de usuário herdado não é descrito como override local.
- Compara o valor do aplicativo com o global quando ambas as leituras são válidas. Uma diferença é oportunidade de comparação controlada; não é um problema de desempenho demonstrado.

Uma consulta ambígua não escolhe um perfil arbitrariamente. Falha em um ID mantém as demais leituras. Valores desconhecidos continuam visíveis em hexadecimal e decimal, sem substituir o valor por um padrão presumido.

## Cobertura de opções públicas

| Grupo | Leituras implementadas | Limite de interpretação |
| --- | --- | --- |
| Energia e frames | Modo de energia, limite de FPS, VSync, máximo de frames pré-renderizados, preferência de atualização | Jogo, API gráfica e modo de apresentação podem controlar o comportamento. Atualização preferida não é a taxa ativa do monitor. |
| OpenGL | Otimização de threads e buffer triplo | Não generalizados para DirectX/Vulkan. |
| Texturas | Qualidade, filtragem anisotrópica/modo/nível, otimização de amostras, otimização trilinear, LOD negativo | Preferências não comprovam qualidade visual ou ganho. |
| Shaders | Cache e tamanho máximo | Tamanho preservado bruto: o cabeçalho não documenta unidade inequívoca para todos os drivers. Não mede cache utilizado ou compilação. |
| Antialiasing | Modo, método, FXAA e MFAA | Depende de suporte do jogo, API, GPU e driver. Métodos não traduzidos preservam o código bruto. |
| Low Latency Mode e Reflex | Cobertura explicitamente não verificável pela API implementada | Máximo de frames pré-renderizados não é usado como substituto para Low Latency Mode ou Reflex. Nenhuma leitura de memória privada do jogo. |

São 19 opções DRS públicas, além de quatro regras de perfis/cobertura. O catálogo define aplicabilidade, origem técnica, hipótese de impacto e forma de validação para cada regra. NVIDIA Profile Inspector e Painel NVIDIA servem de referência funcional; não são dependências nem fontes de IDs privados copiados para a implementação.

A telemetria NVML existente continua separada desta auditoria: utilização, memória, temperatura e clocks disponíveis não demonstram que uma opção DRS foi efetivamente aplicada à renderização.

## ABI e validação

Os testes `NvidiaAuditTests` verificam as declarações independentes dos cabeçalhos: `NVDRS_SETTING_V1` com packing 4 e 12.320 bytes; uniões de 4.100 bytes em offsets 4.120/8.220; perfil de 4.116 bytes; aplicações V1/V2/V3/V4 de 12.296/16.392/16.396/20.492 bytes. Strings UTF-16 têm leitura limitada a 2.048 caracteres. O fallback de aplicação V4 até V1 acontece apenas quando a NVAPI retorna `NVAPI_INCOMPATIBLE_STRUCT_VERSION` (-9).

Há casos para herança, override, diferença global/aplicativo, candidato sem processo, ambiguidade, ID não suportado, falta de permissão, driver ausente, tipo incompatível, cancelamento e liberação da sessão. Esses testes usam um adaptador DRS simulado na fronteira; verificam interpretação e ABI, não o funcionamento de uma GPU NVIDIA real.

```bash
dotnet test tests/PCAnalyzer.Tests/PCAnalyzer.Tests.csproj -c Release --filter FullyQualifiedName~NvidiaAuditTests
```

A validação real depende de um Windows com driver NVIDIA. Conferir o global e o perfil do mesmo executável no Painel NVIDIA, registrar versão do driver e comparar os valores brutos exportados. A verificação do PCAnalyzer informa separadamente se conseguiu ler opções NVIDIA; um runner sem GPU NVIDIA não valida esse caminho. Não foi demonstrado efeito sobre VALORANT, DX11/DX12/Vulkan ou frametime no computador do usuário.

Referências adicionais: [NVAPI DRS](https://docs.nvidia.com/nvapi/group__drsapi.html), [NVIDIA Profile Inspector](https://github.com/Orbmu2k/nvidiaProfileInspector) e [NVML](https://docs.nvidia.com/deploy/nvml-api/latest/).
