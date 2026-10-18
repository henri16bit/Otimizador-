using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

/// <summary>Targeted, versioned catalog. Adding a registry value does not require changing the analysis engine.</summary>
public static class WindowsAuditCatalog
{
    public const string MmcssSource = "https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service";
    public const string SchedulerSource = "https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities";
    public const string MouseSource = "https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow";
    public const string BcdSource = "https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set";
    public const string PowerSource = "https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadacvalueindex";
    private const string InputScope = "Configuração de ponteiro não comprova efeito em jogos com Raw Input; não mede clique-para-fóton.";
    private const string RegistryContract = "Registro é configuração persistente; o uso efetivo por esta versão não foi verificado por API documentada.";
    private const string GeneralValidation = "Registrar cenário e executar capturas comparáveis após eventual mudança externa, sem alteração pelo PC Analyzer.";

    public static IReadOnlyList<AuditRule> Rules { get; } =
    [
        Rule("windows.version", "Otimizações do Windows", "Versão do Windows auditado", "Registro CurrentVersion nas visões 32 e 64 bits", AuditSupport.Documented,
            "Identidade e build do Windows contextualizam os contratos. ProductName pode manter nome legado; não determina sozinho edição ou compatibilidade.",
            "https://learn.microsoft.com/en-us/windows/win32/sysinfo/registry-redirector"),
        Rule("windows.scheduler.separation", "CPU e agendamento", "Política de quantum e primeiro plano", "Registro DWORD de Win32PrioritySeparation", AuditSupport.Conditional,
            "Hexadecimal e decimal são exibidos sem recomendar 0x26 ou 0x2A como universalmente superiores. O campo não descreve prioridades efetivas de threads.",
            "https://learn.microsoft.com/en-us/windows/win32/procthread/priority-boosts"),
        Rule("windows.mmcss.responsiveness", "Otimizações do Windows", "Reserva de CPU do MMCSS", "Registro SystemResponsiveness", AuditSupport.Documented,
            "Aplica-se ao mecanismo MMCSS. Não foi identificada a associação das threads do jogo ao perfil Games.", MmcssSource),
        Rule("windows.mmcss.throttling", "Otimizações do Windows", "Limitação de rede associada a multimídia", "Registro NetworkThrottlingIndex", AuditSupport.NotVerifiable,
            "O contrato desta entrada não consta na página MMCSS consultada. A leitura registra o tweak, mas não comprova reconhecimento neste Windows, limitação ativa ou benefício em jogos.", MmcssSource),
        Rule("windows.mmcss.priority", "Otimizações do Windows", "Prioridade da tarefa MMCSS", "Registro Tasks por perfil", AuditSupport.Documented,
            "Priority de 1 a 8; categoria High sempre interpreta como 2. Exige threads associadas à tarefa MMCSS.", MmcssSource),
        Rule("windows.mmcss.gpu", "Otimizações do Windows", "GPU Priority do MMCSS", "Registro Tasks por perfil", AuditSupport.Ignored,
            "A Microsoft declara que GPU Priority não é utilizado. Não representa prioridade efetiva da GPU NVIDIA.", MmcssSource),
        Rule("windows.mmcss.sfio", "Otimizações do Windows", "SFIO Priority do MMCSS", "Registro Tasks por perfil", AuditSupport.Ignored,
            "A Microsoft declara que SFIO Priority não é utilizado. Não representa prioridade de I/O efetiva.", MmcssSource),
        Rule("windows.mmcss.clock", "Otimizações do Windows", "Clock Rate do MMCSS", "Registro Tasks por perfil", AuditSupport.Conditional,
            "É uma dica de granularidade. A garantia de clock de Vista/Server 2008 foi removida desde Windows 7; não é timer de input nem limite garantido.", MmcssSource),
        Rule("windows.mmcss.category", "Otimizações do Windows", "Categoria da tarefa MMCSS", "Registro Tasks por perfil", AuditSupport.Documented,
            "High, Medium e Low regulam classes de threads associadas à tarefa; perfil existente não comprova adesão do jogo.", MmcssSource),
        Rule("windows.mmcss.affinity", "CPU e agendamento", "Afinidade da tarefa MMCSS", "Registro Tasks por perfil", AuditSupport.Conditional,
            "0 e 0xFFFFFFFF não impõem afinidade segundo a documentação. Não demonstra onde DPC/ISR ou threads do jogo executaram.", MmcssSource),
        Rule("windows.mmcss.background", "Otimizações do Windows", "Execução em segundo plano do MMCSS", "Registro Tasks por perfil", AuditSupport.Documented,
            "Background Only/BackgroundPriority dependem de cliente MMCSS; sem rastreamento de registro das threads.", MmcssSource),
        Rule("windows.ifeo.cpu", "CPU e agendamento", "Prioridade persistente por executável (IFEO)", "Registro IFEO/PerfOptions + processos reais", AuditSupport.NotVerifiable,
            "O catálogo não converte números IFEO em uma promessa de prioridade efetiva. Exibe separadamente a classe observada e a correspondência pelo nome do executável.", SchedulerSource),
        Rule("windows.ifeo.io", "CPU e agendamento", "Prioridade de I/O por executável (IFEO)", "Registro IFEO/PerfOptions", AuditSupport.NotVerifiable,
            "Contrato público e valores válidos desta entrada não foram confirmados para Windows 10 22H2; não são tratados como I/O efetivo.", SchedulerSource),
        Rule("windows.ifeo.page", "RAM e armazenamento", "Prioridade de páginas por executável (IFEO)", "Registro IFEO/PerfOptions", AuditSupport.NotVerifiable,
            "PagePriority persistente não equivale a prioridade de memória observada; nenhuma memória de processo protegido é acessada.", SchedulerSource),
        Rule("windows.gaming.gamemode", "Otimizações do Windows", "Preferências de Modo de Jogo", "Registro GameBar do usuário atual", AuditSupport.NotVerifiable, RegistryContract,
            "https://learn.microsoft.com/en-us/windows/win32/api/gamemode/nf-gamemode-hasexpandedresources"),
        Rule("windows.gaming.hags", "GPU NVIDIA", "Preferência de agendamento de GPU", "Registro GraphicsDrivers/HwSchMode", AuditSupport.NotVerifiable,
            "A presença do DWORD não confirma suporte WDDM, estado HAGS ativo ou benefício. A consulta atual não determina o agendador efetivo.",
            "https://learn.microsoft.com/en-us/windows-hardware/drivers/display/hardware-accelerated-gpu-scheduling"),
        Rule("windows.gaming.fullscreen", "Otimizações do Windows", "Preferências de tela cheia e apresentação", "Registro GameConfigStore e AppCompatFlags/Layers", AuditSupport.NotVerifiable,
            "Flags persistentes não confirmam o modo de apresentação do jogo. Validar com captura PresentMon do executável real.",
            "https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model"),
        Rule("windows.gaming.graphics", "GPU NVIDIA", "Preferência gráfica por aplicativo", "Registro UserGpuPreferences", AuditSupport.NotVerifiable,
            "Caminho e preferência encontrados não comprovam qual GPU o aplicativo utilizou. Nenhuma configuração NVIDIA é inferida deste registro.",
            "https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_6/ne-dxgi1_6-dxgi_gpu_preference"),
        Rule("windows.mouse.settings", "MSI/IRQ/USB", "Velocidade e aceleração do ponteiro", "Registro Control Panel/Mouse", AuditSupport.Conditional, InputScope, MouseSource),
        Rule("windows.mouse.curves", "MSI/IRQ/USB", "Curvas persistentes de aceleração", "Registro SmoothMouseXCurve/SmoothMouseYCurve", AuditSupport.NotVerifiable,
            "Bytes brutos preservados; curva não é tratada como frequência USB ou redução de latência. " + InputScope, MouseSource),
        Rule("windows.mouse.effective", "MSI/IRQ/USB", "Parâmetros atuais do ponteiro", "SystemParametersInfo SPI_GETMOUSE/SPI_GETMOUSESPEED", AuditSupport.Documented, InputScope, MouseSource),
        Rule("windows.input.queue", "MSI/IRQ/USB", "Filas dos drivers de mouse e teclado", "Registro Mouclass/Kbdclass Parameters", AuditSupport.Conditional,
            "Capacidade configurada do buffer não mede ocupação, polling ou input lag. MouseDataQueueSize é documentado; contrato equivalente de teclado precisa validação do driver.",
            "https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/keyboard-and-mouse-class-drivers"),
        Rule("windows.memory.policy", "RAM e armazenamento", "Políticas de memória e paginação", "Registro Memory Management", AuditSupport.Conditional,
            "Valor persistente não comprova pressão de memória, paginação ativa ou melhora em jogos. Correlacionar commit, RAM disponível e atividade de disco.",
            "https://learn.microsoft.com/en-us/windows/client-management/introduction-page-file"),
        Rule("windows.power.policy", "Otimizações do Windows", "Plano ativo e políticas de energia", "PowerGetActiveScheme / PowerReadACValueIndex / PowerReadDCValueIndex", AuditSupport.Documented,
            "Lê índices AC/DC do plano; firmware e driver podem limitar aplicação. Não mede clock ou economia real e não recomenda Desempenho Máximo universal.", PowerSource),
        Rule("windows.power.throttling", "CPU e agendamento", "Preferência persistente de Power Throttling", "Registro PowerThrottlingOff", AuditSupport.NotVerifiable,
            "DWORD persistente não comprova política de energia efetiva de cada processo. A auditoria não observa PROCESS_POWER_THROTTLING_STATE nem aplica políticas.",
            "https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-process_power_throttling_state"),
        Rule("windows.graphics.overlay", "GPU NVIDIA", "Override persistente de overlays do compositor", "Registro Dwm/OverlayTestMode", AuditSupport.NotVerifiable,
            "Tweak identificado sem inferir o estado de MPO ou o modo de apresentação do jogo. A semântica pública do DWORD não foi confirmada para esta versão do Windows.",
            "https://learn.microsoft.com/en-us/windows-hardware/drivers/display/multiplane-overlay-support"),
        Rule("windows.process.scheduling", "CPU e agendamento", "Prioridade e afinidade observadas", "GetPriorityClass / GetProcessAffinityMask via Process", AuditSupport.Documented,
            "Snapshot de processos; afinidade não indica utilização de CPU nem conflito. Grupos de processadores e threads podem ter políticas próprias.", SchedulerSource),
        Rule("windows.services.state", "Otimizações do Windows", "Serviços relevantes ao desempenho", "Win32_Service State/StartMode/ProcessId", AuditSupport.Documented,
            "Serviço ativo não comprova carga; serviço desativado não comprova benefício. Dependências e consumo precisam de avaliação específica.",
            "https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-service"),
        Rule("windows.startup.entries", "Otimizações do Windows", "Entradas de inicialização", "Registro Run/RunOnce e StartupApproved", AuditSupport.Conditional,
            "Entrada não comprova execução nesta sessão. StartupApproved é preservado bruto; não se presume formato binário estável.",
            "https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys"),
        Rule("windows.tasks.entries", "Otimizações do Windows", "Tarefas agendadas relevantes", "Task Scheduler COM: leitura de tarefas registradas", AuditSupport.Documented,
            "Ativação e próximo horário não comprovam consumo; coleta limitada às primeiras 256 tarefas, incluindo subpastas, sem executar tarefas.",
            "https://learn.microsoft.com/en-us/windows/win32/taskschd/registeredtask"),
        Rule("windows.timers.qpc", "Latência e DPC/ISR", "Contador de desempenho QPC", "QueryPerformanceFrequency via Stopwatch", AuditSupport.Documented,
            "Frequência QPC não identifica por si só TSC/HPET e não mede latência de entrada. A fonte física não é inferida.",
            "https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps"),
        Rule("windows.timers.capabilities", "Latência e DPC/ISR", "Capacidade de resolução multimídia", "timeGetDevCaps (somente leitura)", AuditSupport.Documented,
            "Mínimo/máximo suportados não são a resolução atualmente pedida por processos. Desde Windows 10 2004 as garantias são por processo; nenhuma resolução é solicitada.",
            "https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timegetdevcaps",
            "https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod"),
        Rule("windows.timers.current", "Latência e DPC/ISR", "Resolução efetiva por processo", "Sem rastreamento de chamadas timeBeginPeriod nesta captura", AuditSupport.NotVerifiable,
            "Não é inferida de timeGetDevCaps nem de um valor global do kernel. Requer captura específica e interpretação por processo.",
            "https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod"),
        .. new[] { "useplatformclock", "useplatformtick", "disabledynamictick", "tscsyncpolicy" }.Select(name =>
            Rule("windows.bcd." + name, "Latência e DPC/ISR", "BCD: " + name, "bcdedit /enum {current}; somente se já elevado", AuditSupport.Conditional,
                "Opção documentada para depuração. A leitura não identifica a fonte efetiva, não confirma reinicialização e não demonstra benefício em jogos.", BcdSource))
    ];

    private static AuditRule Rule(string id, string category, string name, string method, AuditSupport support, string limits, params string[] sources) =>
        new(id, category, name, method, "Windows; acesso de leitura à origem; módulos protegidos podem exigir elevação explícita.",
            "Windows 10/11 x64; contratos condicionais indicados em cada regra. Referências consultadas em 17/10/2026; leitura real depende da versão e permissões.",
            "Valor bruto, fonte, escopo, tipo e estado da leitura. Para benefício: captura comparável do comportamento.",
            "Esclarecer o mecanismo e avaliar uma hipótese com medições; não há redução de latência prometida.", support, limits, GeneralValidation, sources);
}
