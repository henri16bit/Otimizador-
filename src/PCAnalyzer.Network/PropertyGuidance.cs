namespace PCAnalyzer.Network;

internal sealed record PropertyGuidance(string Function, string Consequences, string Recommendation, string ExpectedImpact)
{
    internal static PropertyGuidance For(string keyword)
    {
        var function = keyword.ToUpperInvariant() switch
        {
            "*INTERRUPTMODERATION" or "INTERRUPTMODERATIONRATE" => "Agrupa interrupções de pacotes para reduzir custo de CPU.",
            "*EEE" or "EEE" or "EEELINKADVERTISEMENT" => "Permite estados de baixo consumo no enlace Ethernet quando suportados pelos dois lados.",
            "*RSS" => "Distribui processamento de recepção entre CPUs compatíveis.",
            "*NUMRSSQUEUES" => "Define número de filas RSS permitido pelo driver.",
            "*RECEIVEBUFFERS" or "RECEIVEBUFFERS" => "Reserva descritores e buffers para recepção.",
            "*TRANSMITBUFFERS" or "TRANSMITBUFFERS" => "Reserva descritores e buffers para transmissão.",
            "*SPEEDDUPLEX" => "Configura velocidade e duplex ou negociação automática do enlace.",
            "*FLOWCONTROL" => "Controla resposta e emissão de quadros PAUSE Ethernet.",
            "*LSOV2IPV4" or "*LSOV2IPV6" => "Transfere segmentação de grandes envios TCP para o adaptador.",
            "*IPCHECKSUMOFFLOADIPV4" or "*TCPCHECKSUMOFFLOADIPV4" or "*TCPCHECKSUMOFFLOADIPV6" or
                "*UDPCHECKSUMOFFLOADIPV4" or "*UDPCHECKSUMOFFLOADIPV6" => "Transfere cálculo e verificação de checksums para o adaptador.",
            "*WAKEONMAGICPACKET" or "*WAKEONPATTERN" => "Permite acordar o computador por eventos suportados da rede.",
            "*PMARPOFFLOAD" or "*PMNSOFFLOAD" => "Permite responder a solicitações específicas durante estados de baixo consumo.",
            _ => "Propriedade divulgada pelo driver instalado. A função exata depende da documentação desse driver."
        };
        return new(function,
            "Pode alterar uso de CPU, energia, throughput, latência ou disponibilidade. O driver pode exigir reinício do adaptador/computador; a conexão pode cair.",
            "Preservar o valor atual sem evidências de problema. Comparar medições repetidas nas mesmas condições antes e depois de uma alteração individual autorizada.",
            "Benefício não quantificado; depende de carga, driver e rede. Não desabilitar RSS/offloads em conjunto nem forçar velocidade sem evidência.");
    }
}
