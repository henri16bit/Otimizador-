using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

internal sealed record NvidiaAuditSetting(uint Id, string Key, string Name, string Applicability,
    Func<uint, string> Format)
{
    internal string RuleId => "nvidia.drs." + Key;
}

internal static class NvidiaAuditSettings
{
    internal const string HeaderSource = "https://github.com/NVIDIA/nvapi/blob/70d337db9186e968eab622f7e786de7e437faf3d/NvApiDriverSettings.h";
    internal const string ApiSource = "https://docs.nvidia.com/nvapi/group__drsapi.html";
    private static string OnOff(uint v) => v switch { 0 => "Desativado", 1 => "Ativado", _ => Unknown(v) };
    private static string Unknown(uint v) => $"Valor público não interpretado: 0x{v:X8} ({v})";

    // Only settings explicitly published by NVIDIA, not private Profile Inspector identifiers.
    internal static IReadOnlyList<NvidiaAuditSetting> All { get; } =
    [
        new(0x1057EB71, "power", "Modo de energia", "Depende da GPU, driver e carga; modo máximo não demonstra ganho.",
            v => v switch { 0 => "Adaptativo", 1 => "Preferir desempenho máximo", 2 => "Controlado pelo driver",
                3 => "Preferir desempenho consistente", 4 => "Preferir consumo mínimo", 5 => "Energia otimizada", _ => Unknown(v) }),
        new(0x10835002, "frame-limit", "Limite de FPS", "Limite DRS; o jogo ou outro limitador pode prevalecer.",
            v => v == 0 ? "Desativado" : v <= 1023 ? $"{v} FPS" : Unknown(v)),
        new(0x00A879CF, "vsync", "Sincronização vertical", "API gráfica, apresentação e opções do jogo condicionam o efeito.",
            v => v switch { 0x60925292 => "Controlada pelo aplicativo", 0x08416747 => "Forçar desativada",
                0x47814940 => "Forçar ativada", 0x32610244 => "Intervalo 2", 0x71271021 => "Intervalo 3",
                0x13245256 => "Intervalo 4", 0x18888888 => "Virtual", _ => Unknown(v) }),
        new(0x007BA09E, "prerender", "Máximo de frames pré-renderizados", "Configuração legada; não identifica Low Latency Mode nem Reflex. DX12/Vulkan controlam suas próprias filas.",
            v => v == 0 ? "Controlado pelo aplicativo" : v <= 255 ? $"{v} frames" : Unknown(v)),
        new(0x20C1221E, "opengl-threads", "Otimização de threads (OpenGL)", "ID OGL_THREAD_CONTROL; não comprova efeito em DirectX ou Vulkan.",
            v => v switch { 0 => "Automática", 1 => "Ativada", 2 => "Desativada", _ => Unknown(v) }),
        new(0x00CE2691, "texture-quality", "Qualidade da filtragem de texturas", "Qualidade e desempenho dependem do conteúdo e da API gráfica.",
            v => v switch { 0xfffffff6 => "Alta qualidade", 0 => "Qualidade", 10 => "Desempenho", 20 => "Alto desempenho", _ => Unknown(v) }),
        new(0x00198FFF, "shader-cache", "Cache de shaders", "Estado do perfil; não mede cache em uso, recompilação ou stuttering.", OnOff),
        new(0x00AC8497, "shader-cache-size", "Tamanho máximo do cache de shaders", "O cabeçalho publica limites, mas não uma unidade inequívoca para todos os drivers; valor preservado sem conversão.",
            v => $"0x{v:X8} ({v}); unidade não inferida"),
        new(0x10D2BB16, "anisotropic-mode", "Modo de filtragem anisotrópica", "O nível depende do modo e do suporte no jogo.",
            v => v switch { 0 => "Controlado pelo aplicativo", 1 => "Definido no driver", 2 => "Condicional", _ => Unknown(v) }),
        new(0x101E61A9, "anisotropic-level", "Nível de filtragem anisotrópica", "Não demonstra filtragem efetiva sem verificar o modo e a renderização.",
            v => v switch { 0 => "Point (sem anisotropia)", 1 => "Linear (sem anisotropia)", 2 or 4 or 8 or 16 => $"{v}x", _ => Unknown(v) }),
        new(0x107EFC5B, "antialias-mode", "Modo de antialiasing", "Overrides podem não ser utilizados pela API gráfica ou pelo jogo.",
            v => v switch { 0 => "Controlado pelo aplicativo", 1 => "Substituir", 2 => "Melhorar", _ => Unknown(v) }),
        new(0x10D773D2, "antialias-method", "Método de antialiasing", "O código bruto é preservado; a enumeração inclui métodos legados e reservados.",
            v => v switch { 0 => "Sem método imposto", 0x0e => "Multisample 2x diagonal", 0x10 => "Multisample 4x",
                0x25 => "Multisample 8x", _ => Unknown(v) }),
        new(0x1074C972, "fxaa", "FXAA", "Pode não prevalecer sobre renderização e antialiasing próprios do jogo.", OnOff),
        new(0x0098C1AC, "mfaa", "MFAA", "Requer suporte de GPU, driver, API e modo de antialiasing compatível.", OnOff),
        new(0x20FDD1F9, "opengl-triple-buffer", "Buffer triplo (OpenGL)", "ID OGL_TRIPLE_BUFFER; não equivale aos buffers DirectX do jogo.", OnOff),
        new(0x00E73211, "anisotropic-sample", "Otimização de amostras anisotrópicas", "Altera uma preferência de filtragem; não mede o resultado visual ou desempenho.", OnOff),
        new(0x002ECAF2, "trilinear", "Otimização trilinear", "Depende de amostragem e qualidade de textura no jogo.", OnOff),
        new(0x0019BB68, "negative-lod", "Bloqueio de LOD negativo", "O estado do perfil não demonstra efeito na API gráfica utilizada.", OnOff),
        new(0x0064B541, "refresh", "Preferência de taxa de atualização", "Não informa a taxa ativa do monitor; verificar modo de exibição e jogo.",
            v => v switch { 0 => "Controlada pelo aplicativo", 1 => "Maior disponível", _ => Unknown(v) })
    ];

    internal static AuditRule Rule(NvidiaAuditSetting setting) => new(setting.RuleId, "NVIDIA", setting.Name,
        "NVAPI DRS GetSetting; valor, origem e indicador de predefinição do driver.",
        "Driver NVIDIA instalado com NVAPI DRS acessível.", "Windows x64; suporte por ID depende da versão do driver.",
        "Leitura DRS bem-sucedida e identificação do perfil e da origem; capturas comparáveis para avaliar impacto.",
        "A preferência pode afetar renderização ou consumo quando aplicável; nenhum ganho é presumido.",
        AuditSupport.Conditional, setting.Applicability,
        "Registrar capturas equivalentes do mesmo jogo/API e comparar frametime, carga, clocks e temperatura após eventual mudança externa.",
        [HeaderSource, ApiSource]);
}
