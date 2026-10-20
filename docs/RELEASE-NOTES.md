# PC Performance Analyzer v0.6.0-preview.3

**ANALISAR MEU PC → escolher uma categoria → comparar atual e recomendação → abrir a explicação → medir quando necessário.** A auditoria existente ganha uma interface compacta, com dados reais e sem alterações nas configurações do computador.

- Dez destinos no menu: Início, Windows, NVIDIA, Rede, MSI / IRQ / USB, Latência, Hardware, Jogos, Recomendações e Avançado.
- Início com uma ação principal, resumo na própria tela e até cinco pontos priorizados. A análise padrão não exige confirmação extra; operações com tráfego, captura ou carga continuam manuais e confirmadas.
- Listas de configurações com valor atual, recomendação e status; busca por nome, grupos, cinco filtros e ordenação. A virtualização evita criar simultaneamente todas as linhas de listas extensas.
- Janela de detalhes com justificativa, impacto possível, confiança, riscos e teste compatível. Valores brutos, Registro, GUIDs e evidências completas ficam em uma seção recolhida. Fechar a janela preserva a categoria e seus filtros.
- Recomendações distinguem manter, comparar e depender de medição. Não há valor ideal inventado. Dispositivos virtuais, código PnP 22 e campos documentados como ignorados deixam de ocupar o destaque de melhorias sem evidência relevante.
- Rede distingue falta de permissão, comando ausente, consulta não suportada e falha de leitura. Valores vazios e funções de energia não suportadas não aparecem como medições bem-sucedidas. A identificação por GUID mantém a proteção contra associar dados ao adaptador errado.
- Resultados de rede, memória, armazenamento e jogos aparecem na seção correspondente, com estado, unidades e detalhes recolhidos. Latência separa DPC/ISR, frametime, RTT e acesso à memória; não calcula input lag total.
- Histórico, exportações, comparações, coletores, importações ETL/CSV, captura manual e ferramentas avançadas foram preservados. Aplicação e restauração de ajustes continuam bloqueadas na interface, API e auxiliar.
- Corrigidos contraste do botão principal e dos textos da explicação, aplicação da fonte da janela, preservação do relatório de auditoria após testes manuais e atualização do estado de leitura da rede. A análise pelo Início permanece passiva mesmo quando opções de testes foram marcadas anteriormente em Avançado.

Baixe **PCAnalyzer-windows-x64.zip** em **Assets**. Os arquivos “Source code” contêm apenas código-fonte.

1. Extraia o ZIP completo para uma pasta nova e gravável.
2. Abra **PCAnalyzer.exe** como usuário normal, mantendo DLLs e subpastas junto dele.
3. Clique em **ANALISAR MEU PC** e consulte as categorias.

O pacote portátil inclui o runtime .NET; não exige instalação separada. O auxiliar solicita UAC para a captura DPC/ISR autorizada. Nenhuma configuração operacional é alterada.

A publicação exige compilação sem avisos, testes de lógica, empacotamento, cenários WPF e verificação passiva no runner Windows. Os resultados efetivamente obtidos ficam em `docs/VALIDATION.md`; a existência dessas verificações não equivale à validação do computador alvo.

Permanecem necessárias as verificações no Windows 10 Pro 22H2 do usuário: NVIDIA, Intel I219-V, NVMe, ETW/PresentMon, Vanguard e escala real do Windows em 125% e 150%. Layout automatizado e dados de teste não comprovam funcionamento desses dispositivos nem ganhos em jogos. Leituras sem suporte, SPD/timings/canais, input lag físico e outros limites estão em `docs/AUDIT-EXPERIENCE.md` e `docs/PERFORMANCE-CONSULTANCY.md`.

O executável ainda não é assinado. Se a abertura falhar, consulte `%LOCALAPPDATA%\PCAnalyzer\logs\startup-last-error.txt`. Confira o download com `Get-FileHash .\PCAnalyzer-windows-x64.zip -Algorithm SHA256` e compare com o arquivo `.sha256`.
