# PC Performance Analyzer v0.7.0-preview.3

**Valor atual → recomendação fundamentada → motivo curto → investigação manual quando necessária.** Esta versão evolui o motor de recomendações e a interpretação das medições, preservando as categorias e a auditoria sem aplicação de ajustes.

- Cada propriedade recebe uma classificação funcional antes de gerar uma recomendação. Informações de identificação, recursos sem suporte, leituras incompletas e propriedades desconhecidas deixam de virar oportunidades apenas por estarem presentes. Sem regra aplicável, a interface informa **Sem recomendação fundamentada**.
- As listas mostram o valor detectado e um motivo curto. `*IfType = 6` aparece como Ethernet, Win32PrioritySeparation preserva hexadecimal e decimal, e cada processo mantém sua identidade, prioridade e afinidade observadas.
- As recomendações passam a considerar o contexto: 2933 MT/s só recebe a explicação específica de i7-10700K/B460 quando CPU e placa foram identificadas; moderação de interrupções considera o custo de CPU; perfis NVIDIA distinguem global, aplicação e herança. Não há recomendação universal de prioridade, afinidade ou potência máxima.
- A janela de detalhes mostra justificativa, impacto possível, confiança da recomendação e validação. Evidências e riscos ficam recolhidos; a navegação técnica usa páginas de até 20 entradas, com uma rolagem principal e ações no rodapé.
- **ANALISAR MEU PC** primeiro conclui as leituras de inventário, respeitando falhas e limites de cada fonte, e depois inicia a janela de observação passiva. O tempo selecionado não limita artificialmente todo o inventário. Cancelamento e coleta parcial preservam somente as evidências obtidas.
- **INVESTIGAR DESEMPENHO** leva aos testes manuais. DPC/ISR permite 30, 60 ou 120 segundos, com objetivo, contexto e confirmação. Navegar até um teste não inicia uma captura.
- DPC/ISR passa a apresentar conclusão e próximo passo a partir de duração, contagens, perdas, distribuição, recorrência e contexto. Máximo, média, p95 e p99 continuam separados. Um pico de 1 ms não é tratado como defeito universal; uma captura curta não comprova baixo input lag.
- A associação entre módulos ETW e dispositivos usa identidades disponíveis do driver. Caminhos conflitantes, binários compartilhados, nomes candidatos e inventários antigos ficam explícitos. MSI, afinidade e distribuição por CPU não demonstram causalidade por si só.
- O Início reúne cobertura, até cinco prioridades entre configurações e achados de medições, itens adequados e últimas medições válidas com data e contexto. Uma tentativa posterior com falha não apaga a última medição válida.
- Resultados, interpretação e próximo passo aparecem na categoria do teste. Abrir e exportar selecionam o resultado daquela categoria. Os novos campos são persistidos no histórico e incluídos nas exportações, com anonimização e escape HTML; registros antigos continuam legíveis.

A publicação exige testes de lógica, cenários WPF, verificação passiva no Windows e abertura do executável empacotado. Evidências e limites estão em [VALIDATION.md](https://github.com/henri16bit/Otimizador-/blob/main/docs/VALIDATION.md); o mapeamento dos 20 requisitos de regressão está em [RECOMMENDATION-ENGINE.md](https://github.com/henri16bit/Otimizador-/blob/main/docs/RECOMMENDATION-ENGINE.md).

Baixe o executável no arquivo **PCAnalyzer-windows-x64.zip**, em **Assets** da release. “Source code” contém apenas código-fonte.

1. Extraia o ZIP completo para uma pasta nova e gravável.
2. Abra **PCAnalyzer.exe** como usuário normal, mantendo DLLs e subpastas junto dele.
3. Use **ANALISAR MEU PC** para a análise passiva e escolha uma investigação quando houver um objetivo concreto.

O pacote portátil inclui o runtime .NET. A captura DPC/ISR autorizada pode solicitar UAC pelo auxiliar. Aplicação e restauração de ajustes permanecem bloqueadas.

Ainda é necessário validar os dispositivos e cenários do computador alvo com Windows 10 Pro 22H2: NVIDIA, Intel I219-V, NVMe, ETW/PresentMon, Vanguard e escala real em 125% e 150%. O runner Windows e as fixtures não substituem essa validação. Benefício de desempenho não foi medido; a classificação de anomalia fundamentada em DPC/ISR permanece reservada enquanto não houver referência aplicável ou evidência independente. A ferramenta não mede input lag físico.

O executável ainda não é assinado. Se a abertura falhar, consulte `%LOCALAPPDATA%\PCAnalyzer\logs\startup-last-error.txt`. Confira o download com `Get-FileHash .\PCAnalyzer-windows-x64.zip -Algorithm SHA256` e compare com o arquivo `.sha256` publicado.
