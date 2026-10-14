# Dependências e licenças

O projeto utiliza somente APIs de usuário e não instala drivers kernel próprios. As versões exatas, dependências transitivas e hashes de conteúdo NuGet estão nos `packages.lock.json` de cada projeto. TLS, verificação de assinaturas NuGet e hashes oficiais do SDK permanecem habilitados.

| Componente | Versão usada | Licença / origem | Uso e compatibilidade |
| --- | --- | --- | --- |
| .NET SDK | 10.0.402 | [MIT, dotnet/sdk](https://github.com/dotnet/sdk) | Compilação Linux/Windows; runtime 10.0.13. SDK verificado pelo SHA-512 do manifesto oficial Microsoft. |
| WPF / Windows Desktop | .NET 10 | [MIT, dotnet/wpf](https://github.com/dotnet/wpf) | Interface apenas no Windows; compilação cruzada habilitada. |
| System.Management | 10.0.13 | [MIT, dotnet/runtime](https://github.com/dotnet/runtime) | WMI em Windows; falhas/ausência de provedor tratadas por componente. |
| System.Diagnostics.PerformanceCounter | 10.0.13 | [MIT, dotnet/runtime](https://github.com/dotnet/runtime) | Contadores do Windows com descoberta de nomes localizados. |
| Microsoft.Data.Sqlite | 10.0.13 | [MIT, dotnet/efcore](https://github.com/dotnet/efcore) | Histórico local; inclui SQLitePCLRaw e SQLite nativo via pacotes transitivos. |
| SQLitePCLRaw | Ver lockfile | [Apache-2.0, ericsink/SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) | Interop; engine [SQLite em domínio público](https://www.sqlite.org/copyright.html). |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.2.8 | [MIT, microsoft/perfview](https://github.com/microsoft/perfview) | Captura/decodificação ETW no Windows; testes da agregação independem de Windows. |
| Microsoft.NET.Test.Sdk | 18.11.0 | [MIT, microsoft/vstest](https://github.com/microsoft/vstest) | Somente desenvolvimento/testes. |
| xUnit.net | 2.9.3 | [Apache-2.0, xunit/xunit](https://github.com/xunit/xunit) | Somente testes. |
| xunit.runner.visualstudio | 3.1.5 | [Apache-2.0, xunit/visualstudio.xunit](https://github.com/xunit/visualstudio.xunit) | Somente testes. |
| NVIDIA NVML | API da DLL do driver instalado | [NVIDIA NVML](https://docs.nvidia.com/deploy/nvml-api/latest/) — termos do driver NVIDIA | DLL não distribuída pelo aplicativo. Carga dinâmica apenas de System32/NVSMI; não é requisito para GPUs de outro fabricante. |
| Intel PresentMon Console | Opcional, versão/ajuda verificadas em execução | [MIT, GameTechDev/PresentMon](https://github.com/GameTechDev/PresentMon) | Executável não redistribuído. CLI limitada por tempo/PID e importação dos campos efetivamente presentes. |
| PowerShell NetAdapter / PowrProf / Win32 | Recursos do Windows | Microsoft Windows | Operações tipadas; nenhuma entrada executada como comando arbitrário. |

MVVM, gráficos WPF e logger JSON são implementados no projeto, sem pacote externo de UI. LibreHardwareMonitor não foi incluído: os sensores dependentes de drivers auxiliares ainda não foram implementados. Não se promete suporte ou precisão desses sensores.

Referências de API e limitações específicas estão em [hardware](docs/hardware.md), [ETW](docs/tracing.md), [Gaming](docs/gaming.md) e [rede](docs/network-optimization.md). A manutenção/verificação foi feita pelas versões estáveis disponíveis no NuGet e código/documentação original; isso não equivale a uma auditoria das dependências.
