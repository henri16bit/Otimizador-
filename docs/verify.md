# Verificação manual no Windows

`PCAnalyzer.Verify` é um executável de aceitação de leituras passivas. Só começa quando invocado explicitamente. Não executa ping, benchmark, stress, escrita de teste em SSD, alteração de rede/energia, ETW ou PresentMon. Execute com usuário normal no Windows 10 Pro 22H2 x64 compatível com .NET 10.

Na raiz do repositório, com SDK .NET 10:

```powershell
dotnet run --project src/PCAnalyzer.Verify -- --output "$env:LOCALAPPDATA\PCAnalyzer\validation"
```

Ou inicie `PCAnalyzer.Verify.exe` publicado para Windows x64. Sem `--output`, usa `%LOCALAPPDATA%\PCAnalyzer\validation`; `--help` mostra uso. Cada execução cria `run-<id da sessão>`, preservando verificações anteriores. Ctrl+C cancela e libera o coletor.

A ferramenta identifica o computador por WMI/Win32/NVML e coleta entre três e seis lotes reais com espera de 2 s. O primeiro estabelece deltas; os dois últimos exigem CPU total, RAM total/disponível, commit e limite de commit disponíveis e finitos. Confere também PID próprio, nome, working set positivo e CPU finita após aquecimento. Não exige acesso a processos protegidos de terceiros. O limite de seis lotes permite iniciar fontes opcionais sem esperar indefinidamente. Métricas ausentes e seus motivos também são persistidos.

Quando NVIDIA é identificada, exige enumeração NVML e utilização/VRAM usada/VRAM total em cada GPU NVML nas duas últimas amostras. Não presume que um índice WMI identifica o mesmo índice NVML. Se NVML estiver ausente ou não oferecer sensores essenciais, retorna verificação incompleta. Em máquinas sem NVIDIA identificada, informa explicitamente que telemetria GPU não foi validada nesta execução.

Os artefatos são:

- `validation.db`: sessão local completa no SQLite, incluindo inventário e todos os metadados reais. Dados locais completos permanecem no banco; compartilhe os relatórios anônimos.
- `session.anonymous.json`: exportação anônima da sessão efetivamente recarregada do banco.
- `session.anonymous.csv`: representação completa dos campos do JSON por caminhos RFC 6901, não apenas valores do painel.
- `session.anonymous.html`: relatório legível da mesma sessão salva.
- `validation-result.json`: verificações, resultados booleanos, escopo e campos ainda não validados, para anexar à aceitação.

A comparação SQLite serializa a sessão inteira antes e depois e exige igualdade exata; isso inclui valores numéricos, nulos, estados, fonte, método, timestamp, intervalo e agregação. O JSON anônimo é desserializado e comparado ao snapshot anônimo do registro salvo; valores/estados/metadados temporais também são confrontados com os originais. O CSV é lido de volta, interpretando aspas e quebras de linha, e seus campos são comparados ao JSON exportado. O arquivo HTML é conferido contra a exportação gerada a partir do registro recarregado. Nenhum valor é preenchido para satisfazer o teste.

| Código de saída | Significado |
| --- | --- |
| 0 | Leituras essenciais e persistência/exportações verificadas dentro deste escopo. Não significa aceitação integral do aplicativo. |
| 1 | Argumentos inválidos ou falha na descoberta/coleta. |
| 2 | Sistema não Windows; nenhuma API de hardware Windows foi executada nem validada. |
| 3 | Uma leitura essencial ou telemetria NVIDIA requerida ficou indisponível/ inválida. Os artefatos reais são preservados. |
| 4 | Persistência, recarga ou relatório falhou; não há declaração de equivalência. |
| 130 | Cancelamento explícito; resultado interrompido não é aceito. |

O utilitário confirma que APIs retornaram medições reais, que os estados são respeitados e que os relatórios correspondem ao histórico. Isso não prova precisão dos sensores ou desempenho esperado. Compare os mesmos períodos com PerfMon, Gerenciador de Tarefas e `nvidia-smi` no computador alvo; registre carga, condições e ferramentas de referência. WPF, ETW/DPC por driver, PresentMon, medições físicas de latência, mudanças/reversão e compatibilidade com Vanguard exigem procedimentos separados.

No ambiente Linux, apenas compilação, testes portáveis e o código de saída 2 podem ser verificados. Não apresente esse resultado como validação em Windows.
