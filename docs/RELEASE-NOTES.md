# PC Performance Analyzer — prévia para Windows x64

Diagnósticos integrados ao dashboard: escolha o teste, leia a finalidade, acompanhe etapa/tempo e veja o resultado na mesma tela. A conclusão não muda sua página. Resultados, falhas e cancelamentos ficam no histórico, com reabertura, comparação e exportação completa.

Rede em três passos: escolher conexão, testar com ping e entender propriedades. A consulta usa apenas o adaptador escolhido, inclui driver/link/RSS/energia e explica propriedades sem suporte. Ajustes exigem prévia, confirmação, backup e releitura; mudanças desde a prévia confirmada bloqueiam a aplicação.

Ajuda central para métricas, gráficos de ping e DPC/ISR, triagem de variação de frames e evidências temporais quando há UTC conhecido. Discos têm seleção individual. Ausências ficam explícitas, sem estimativas de saúde. Benchmark GPU, sensores CPU/SMART e alinhamento de relógios relativos seguem indisponíveis.

Correção da inicialização: a barra de progresso agora recebe o valor em uma única direção. A ligação anterior herdava o modo de escrita do WPF e tentava atualizar uma propriedade com setter privado, causando uma exceção ao construir a janela.

Correção do fechamento: a janela aguarda o retorno do evento de fechamento antes de concluir a saída após liberar os recursos, inclusive quando essa liberação termina imediatamente.

Baixe **PCAnalyzer-windows-x64.zip** na seção **Assets** abaixo. Os arquivos automáticos “Source code” contêm apenas o código-fonte.

1. Extraia o ZIP completo para uma pasta gravável.
2. Abra **PCAnalyzer.exe** na pasta extraída.
3. Mantenha as DLLs e as subpastas `worker`, `verify` e `docs` junto ao aplicativo.

O pacote é portátil e inclui o runtime .NET. A abertura inicia coleta passiva; operações privilegiadas solicitadas pelo usuário pedem autorização pelo UAC.

Esta é uma prévia: a execução da interface e as integrações no computador Windows ainda precisam de validação local. O Windows 10 Pro 22H2 solicitado é um alvo de compatibilidade técnica pendente; consulte o README sobre o suporte do .NET 10. O executável ainda não tem assinatura digital.

O processo de publicação exige compilação sem avisos, testes automatizados, verificação do ZIP e abertura WPF no Windows. Além de oito páginas, sete abas internas e tamanho compacto, um executável de teste separado exercita progresso no dashboard, navegação durante execução, cancelamento, falha e reabertura persistida. Suas fixtures não entram no pacote. Isso não substitui a validação dos sensores, ETW/PresentMon ou ajustes no seu computador.

Se a abertura falhar, as falhas gerenciadas exibem uma mensagem e salvam os detalhes em `%LOCALAPPDATA%\PCAnalyzer\logs\startup-last-error.txt` (ou na pasta temporária, caso a primeira pasta não seja gravável).

O arquivo `.sha256` permite conferir o download com `Get-FileHash .\PCAnalyzer-windows-x64.zip -Algorithm SHA256` no PowerShell. Consulte `docs/WINDOWS-VALIDATION.md` no pacote para a validação local.
