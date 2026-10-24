namespace PCAnalyzer.Core;

public static partial class AuditRecommendationClassifier
{
    private static AuditRecommendation? Nvidia(AuditAssessment a)
    {
        var o = a.Observation;
        if (!o.RuleId.StartsWith("nvidia.", StringComparison.Ordinal)) return null;
        if (!o.RuleId.StartsWith("nvidia.drs.", StringComparison.Ordinal))
            return Unknown(o.RuleId == "nvidia.reflex" ? "O estado interno de Reflex no jogo não foi verificado por esta leitura." :
                "Este recurso não possui estado efetivo e contrato verificáveis nesta origem.");
        var api = (Fact(o, "GraphicsApi") ?? Fact(o, "GraphicsAPI") ?? Fact(o, "Api") ?? "").ToLowerInvariant();
        var name = o.RuleId["nvidia.drs.".Length..];
        var common = " A origem DRS e a herança do perfil permanecem nos detalhes; o valor do perfil não comprova uso efetivo pela aplicação.";
        var validate = "Confirme o executável e perfil do jogo, API gráfica e modo de apresentação; compare frametimes sob a mesma carga.";
        if (name.StartsWith("opengl-", StringComparison.Ordinal) && (api.Contains("direct") || api.Contains("vulkan") || api is "dx11" or "dx12"))
            return Info("Esta opção é específica de OpenGL e não descreve a API identificada para o aplicativo.",
                "Use as opções da API realmente utilizada; não atribua efeito DirectX/Vulkan a uma preferência OpenGL.");
        var policy = name switch
        {
            "power" => Keep("O modo de energia do perfil não demonstra limitação de clocks durante o jogo.",
                "Preferir desempenho máximo não é superior para todos os aplicativos; pode elevar consumo e temperatura sem melhorar frametime." + common,
                "Pode mudar comportamento de clocks quando aplicável; ganho não foi medido.",
                "Observe clocks, temperatura, utilização e frametimes do jogo antes de comparar o modo de energia.",
                risks: "Consumo, calor e ruído podem aumentar.", test: "frames"),
            "frame-limit" => Keep("O limite do driver pode coexistir com o limitador do jogo ou da apresentação.",
                "O valor DRS identifica uma preferência de FPS, não a cadência efetiva. Escolher outro limite exige taxa do monitor, objetivo de latência e política de sincronização." + common,
                "Pode alterar consumo e cadência; não há limite universal para menor latência.", validate, test: "frames"),
            "vsync" => Keep("A sincronização depende da API, do monitor e da política do jogo.",
                "O valor do perfil pode ser delegado ao aplicativo ou substituído pelo caminho de apresentação. Ele não prova por si só atraso de entrada." + common,
                "Pode trocar tearing, cadência e espera; benefício depende do objetivo de apresentação.", validate, test: "frames"),
            "prerender" when api is "dx12" || api.Contains("direct3d 12") || api.Contains("vulkan") =>
                Info("DX12/Vulkan controlam suas próprias filas; este campo legado não prova limitação da fila do jogo.",
                    "Verifique opções internas do jogo e capturas de frames; não trate este ID como Reflex ou Low Latency Mode."),
            "prerender" => Keep("Este campo legado não identifica Reflex nem Low Latency Mode.",
                "A aplicabilidade depende da API; DX12/Vulkan gerenciam filas próprias. API não identificada impede afirmar efeito no jogo." + common,
                "Pode afetar filas em caminhos compatíveis; input lag físico não foi medido.", validate, test: "frames"),
            "opengl-threads" => Keep("O controle de threads é específico de OpenGL; não é otimização geral de CPU.",
                "Sem confirmar OpenGL no executável, não há base para atribuir efeito ao valor. Mesmo em OpenGL, carga e driver condicionam o resultado." + common,
                "Pode alterar trabalho no driver OpenGL; ganho no jogo não demonstrado.", validate, test: "frames"),
            "opengl-triple-buffer" => Keep("Buffer triplo OpenGL não equivale à fila de apresentação DirectX.",
                "O efeito requer OpenGL e uma configuração de apresentação compatível. Um perfil existente não confirma esse caminho." + common,
                "Pode alterar espera e cadência em OpenGL; efeito em outro caminho não inferido.", validate),
            "shader-cache" => Keep("A preferência de cache não demonstra recompilações ou stuttering na captura.",
                "O estado do perfil não comprova que shaders estão sendo recompilados. Cache aquecido, versão do driver e conteúdo precisam ser considerados." + common,
                "Reutilizar shaders pode reduzir trabalho repetido quando aplicável; benefício não foi comparado.",
                "Compare passagens equivalentes com cache frio e aquecido e preserve versão do driver e conteúdo.", test: "frames"),
            "shader-cache-size" => Unknown("A unidade e a aplicabilidade deste limite não foram estabelecidas para o driver; não se recomenda um tamanho arbitrário."),
            "refresh" => Keep("A preferência não informa a taxa de atualização ativa do monitor.",
                "O jogo pode escolher um modo distinto. A opção 'Maior disponível' no perfil não comprova que o monitor está operando nessa taxa." + common,
                "A taxa ativa pode alterar a cadência da tela; a leitura DRS isolada não mede esse efeito.",
                "Confira a taxa ativa do monitor e o modo de exibição do jogo antes de recomendar outra preferência."),
            "texture-quality" => ImageQuality("A qualidade de filtragem troca fidelidade visual por trabalho de amostragem."),
            "anisotropic-mode" => ImageQuality("O modo define quem controla a anisotropia; a renderização pode continuar sob controle do jogo."),
            "anisotropic-level" => ImageQuality("O nível depende do modo de filtragem e do suporte no conteúdo do jogo."),
            "antialias-mode" => ImageQuality("Overrides de antialiasing podem ser ignorados pela API ou pelo jogo."),
            "antialias-method" => ImageQuality("O método só importa se o modo e a API permitirem sua aplicação."),
            "fxaa" => ImageQuality("FXAA pode alterar nitidez; a preferência não demonstra aplicação efetiva."),
            "mfaa" => ImageQuality("MFAA exige combinação compatível de GPU, driver, API e antialiasing."),
            "anisotropic-sample" => ImageQuality("Otimização de amostras pode trocar qualidade de filtragem por trabalho de GPU."),
            "trilinear" => ImageQuality("Otimização trilinear depende do conteúdo e da filtragem utilizada pelo jogo."),
            "negative-lod" => ImageQuality("O bloqueio de LOD afeta amostragem quando aplicável; não é ajuste direto de input lag."),
            _ => Unknown()
        };
        return Invalid(o) && policy.FunctionalKind is not (AuditFunctionalKind.Incomplete or AuditFunctionalKind.Informational)
            ? Contract(o) : policy;
    }
    private static AuditRecommendation ImageQuality(string explanation) => Keep(explanation,
        explanation + " O perfil e sua herança não comprovam efeito na API usada nem superioridade da alternativa.",
        "Pode alterar qualidade visual e custo de GPU; a direção e o tamanho do efeito não foram medidos.",
        "Confirme aplicação no executável correto e compare imagem e frametimes da mesma cena, sem mudar vários ajustes simultaneamente.",
        test: "frames");
}
