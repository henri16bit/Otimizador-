# PROJETO: PC PERFORMANCE ANALYZER
## Aplicativo profissional de diagnóstico avançado, análise de latência e otimização de computadores Windows

### 1. SUA FUNÇÃO

Atue como um engenheiro de software sênior especializado em:

- Desenvolvimento de aplicativos nativos para Windows.
- Engenharia de performance e diagnóstico de sistemas operacionais.
- C#/.NET, WPF, MVVM e arquitetura modular.
- Windows Internals, ETW, WPR e análise de DPC/ISR.
- Diagnóstico de CPU, GPU, RAM, SSD NVMe e drivers.
- Redes Windows, drivers Ethernet Intel e latência de comunicação.
- Análise de frametime, FPS, stuttering e jogos competitivos.
- Benchmarking, telemetria, análise estatística e identificação de gargalos.

Seu objetivo é **PROJETAR, IMPLEMENTAR, TESTAR E ENTREGAR UM APLICATIVO DESKTOP REAL E FUNCIONAL**.

Não quero apenas um protótipo, um design de interface, uma documentação teórica ou um conjunto de scripts PowerShell.

Quero um projeto de software completo, organizado, expansível e capaz de coletar informações reais do Windows.

Você deve trabalhar diretamente no projeto, criar os arquivos, implementar os componentes, testar o que for possível e corrigir os problemas encontrados.

**Prioridade absoluta: precisão das análises, confiabilidade das medições e utilidade real dos diagnósticos.**

Uma interface sofisticada nunca deve ter prioridade sobre a implementação dos mecanismos de análise.

---

### 2. OBJETIVO CENTRAL

Desenvolver uma ferramenta especializada em investigar profundamente o desempenho do computador, principalmente para jogos competitivos como VALORANT.

O aplicativo deverá responder a perguntas como:

1. Meu computador está funcionando dentro do desempenho esperado para meu hardware?
2. Existe algum gargalo relevante em CPU, GPU, RAM, armazenamento ou sistema operacional?
3. Existem indícios de problemas de latência, DPC/ISR, agendamento ou interrupções?
4. Quais processos ou drivers podem estar interferindo no desempenho?
5. Meu sistema apresenta problemas de frametime, stuttering ou instabilidade?
6. Minha placa de rede está configurada adequadamente para jogos competitivos?
7. Quais configurações realmente merecem ser otimizadas?
8. Quais alterações têm evidências de benefício e quais são apenas mitos?
9. Como comparar o computador antes e depois de uma otimização?
10. Como restaurar alterações que não produziram resultados positivos?

O diferencial do aplicativo será a **interpretação inteligente de medições reais**.

Não basta mostrar que a CPU está em 70% de utilização. O aplicativo deverá investigar se aquilo é normal, se existe saturação de threads, qual processo está envolvido e se há correlação temporal com alguma perda de desempenho.

Não basta mostrar um pico de DPC. O aplicativo deverá investigar o driver associado, a duração, a recorrência, o momento do evento e sua possível relação com outros indicadores.

A ferramenta deverá apresentar conclusões tecnicamente justificadas.

---

### 3. COMPUTADOR INICIAL DE DESENVOLVIMENTO

Desenvolva e otimize inicialmente para a seguinte configuração:

**Sistema operacional:**
- Windows 10 x64.

**Processador:**
- Intel Core i7-10700K.
- 8 núcleos físicos.
- 16 threads.

**Placa-mãe:**
- ASUS TUF Gaming B460-PLUS.

**Placa de vídeo:**
- GALAX NVIDIA GeForce RTX 3060.
- 12 GB de VRAM.

**Memória RAM:**
- 16 GB DDR4.
- Dois módulos de 8 GB.
- Linha XPG SPECTRIX D45G RGB.
- Frequência nominal informada: 3600 MT/s.
- Frequência utilizada: 2933 MT/s.
- A frequência de operação é limitada pela combinação de placa-mãe/chipset e processador.
- O aplicativo não deverá classificar automaticamente essa condição como defeito ou configuração incorreta.
- O SKU exato dos módulos, timings efetivos e modo dual-channel devem ser verificados pelo software, quando possível.

**Armazenamento:**
- Dois SSDs NVMe.
- Os modelos exatos deverão ser identificados automaticamente.

**Rede:**
- Intel Ethernet Connection I219-V.

**Monitor:**
- 240 Hz.
- Detectar a frequência efetiva e demais características disponíveis.

**Jogo prioritário:**
- VALORANT.

Embora a primeira versão seja focada nesse computador, a arquitetura deverá permitir adaptação futura para outros componentes e computadores Windows.

Não codifique identificadores de hardware de maneira fixa quando for possível realizar a descoberta automática.

---

### 4. TECNOLOGIAS E ARQUITETURA

Minha preferência é um aplicativo desktop nativo em C#/.NET com WPF.

Utilize uma versão estável e adequadamente compatível com Windows 10. Considere .NET 10, verificando previamente requisitos de sistema, dependências e compatibilidade das bibliotecas escolhidas.

Tecnologias sugeridas:

- C# e .NET.
- WPF para interface desktop.
- MVVM para separação de responsabilidades.
- CommunityToolkit.Mvvm, se adequado.
- SQLite para histórico de diagnósticos e otimizações.
- Serilog ou solução equivalente para logs.
- Windows Management Instrumentation (WMI/CIM).
- Windows Performance Counters.
- APIs Win32 e Windows SDK.
- ETW/Event Tracing for Windows.
- Windows Performance Recorder/Toolkit.
- PresentMon para análise de frames.
- LibreHardwareMonitorLib para sensores compatíveis.
- NVIDIA NVML para telemetria da GPU.
- PowerShell NetAdapter para informações e alterações compatíveis da rede.
- Uma biblioteca de gráficos adequada para aplicações WPF.

Verifique a manutenção, licença, documentação e compatibilidade de todas as dependências.

Não instale drivers próprios em modo kernel apenas para obter medições.

Prefira interfaces documentadas, estáveis e não invasivas.

Estruture a solução em projetos separados, por exemplo:

- `PCAnalyzer.App` — interface WPF.
- `PCAnalyzer.Core` — contratos, modelos e regras de negócio.
- `PCAnalyzer.Hardware` — descoberta de hardware e sensores.
- `PCAnalyzer.Diagnostics` — diagnósticos e ETW.
- `PCAnalyzer.Gaming` — análise de jogos e frametimes.
- `PCAnalyzer.Network` — diagnóstico e configurações de rede.
- `PCAnalyzer.Optimization` — recomendações, aplicação e reversão.
- `PCAnalyzer.Storage` — histórico e persistência.
- `PCAnalyzer.Reports` — exportação de resultados.
- `PCAnalyzer.Tests` — testes automatizados.

A estrutura poderá ser ajustada se houver alternativa tecnicamente superior.

A interface não deverá acessar hardware diretamente. Utilize abstrações, interfaces de serviços e injeção de dependência.

Todas as operações demoradas devem ser assíncronas, canceláveis quando tecnicamente possível e isoladas da thread de interface.

---

### 5. SISTEMA DE DESCOBERTA DE HARDWARE

Ao abrir o programa, realizar uma identificação inicial, leve e predominantemente passiva do computador.

Coletar, quando disponíveis:

**Sistema:**
- Nome e versão do Windows.
- Build instalada.
- Arquitetura.
- Tempo de atividade.
- Informações da BIOS/UEFI.
- Configurações relevantes de energia.
- Modo de inicialização, quando disponível.

**Processador:**
- Fabricante e modelo.
- Núcleos e threads.
- Frequências reportadas.
- Recursos disponíveis.
- Topologia lógica.

**Placa-mãe:**
- Fabricante.
- Modelo.
- Versão da BIOS.
- Chipset, quando identificável.

**Memória:**
- Capacidade.
- Quantidade de módulos.
- Slots ocupados.
- Fabricantes e modelos, quando disponíveis.
- Velocidade nominal e configurada.
- Timings, SPD e XMP, quando acessíveis.
- Identificação do modo de canais, quando tecnicamente verificável.

Não confundir dados do SPD com a configuração efetivamente utilizada.

**GPU:**
- Modelo e fabricante.
- Quantidade de memória.
- Versão do driver.
- Informações disponíveis sobre clocks e recursos.

**Armazenamento:**
- Quantidade de unidades físicas.
- Modelos de SSD.
- Interface.
- Capacidade.
- Volumes e partições.
- Identificação do SSD do Windows.
- Localização do VALORANT, quando detectável.
- Localização do arquivo de paginação.

**Rede:**
- Adaptadores instalados.
- Adaptador em uso.
- Driver e versão.
- Velocidade negociada.
- Configurações avançadas suportadas.

**Monitores:**
- Resolução.
- Frequência atual.
- Frequências suportadas quando detectáveis.
- Monitor principal.
- Recursos relevantes de apresentação disponíveis.

Registrar a fonte e a confiabilidade de cada informação.

Se a leitura for impossível, indicar claramente a indisponibilidade.

---

### 6. PAINEL TÉCNICO PRINCIPAL

Desenvolver uma interface dark, moderna, profissional e de alta densidade informacional.

Inspiração funcional: ferramentas profissionais de monitoramento e engenharia de performance.

Não quero uma interface excessivamente simplificada.

A tela inicial deverá apresentar:

- Resumo do hardware.
- Estado da coleta.
- Estado das ferramentas auxiliares.
- Principais métricas disponíveis.
- Histórico de atividade.
- Alertas de possíveis problemas.
- Recomendações pendentes.
- Acesso aos diagnósticos aprofundados.

Criar seções:

1. Dashboard.
2. CPU.
3. GPU.
4. RAM.
5. NVMe.
6. Rede.
7. DPC/ISR.
8. Latência.
9. Análise de jogos.
10. Processos e Windows.
11. Testes.
12. Otimizações.
13. Histórico e relatórios.
14. Configurações.

Utilizar gráficos temporais interativos, tabelas técnicas, filtros, painéis expansíveis e ferramentas de comparação.

O aplicativo deverá informar as unidades utilizadas, os intervalos de amostragem e a origem de cada métrica.

Evitar efeitos visuais que aumentem desnecessariamente o consumo de recursos.

---

### 7. MÓDULO DE CPU — DIAGNÓSTICO PROFUNDO

Investigar:

- Utilização total e por processador lógico.
- Consumo de CPU por processo.
- Frequência atual e efetiva, quando disponíveis.
- Temperaturas e sensores compatíveis.
- Estados de desempenho.
- Possíveis condições de throttling.
- Limitações térmicas ou energéticas, quando detectáveis.
- Pressão de agendamento.
- Atividade de threads.
- Mudanças de contexto.
- Distribuição de carga.
- Atividade de interrupções.
- Relação entre carga de CPU e queda de desempenho.
- Possível saturação de threads relevantes para jogos.

Implementar regras para evitar conclusões incorretas.

Exemplo: utilização total de CPU relativamente baixa não elimina a possibilidade de gargalo em uma thread crítica.

Investigar relações entre frequência, utilização, temperatura e eventos de performance antes de sugerir causas.

Não concluir que existe throttling apenas porque a frequência está abaixo do turbo máximo anunciado.

---

### 8. MÓDULO DE GPU

Utilizar NVML ou outras interfaces apropriadas e documentadas.

Investigar:

- Utilização da GPU.
- Consumo de VRAM.
- Temperatura.
- Frequências.
- Estados de desempenho.
- Potência, quando disponível.
- Limitações reportadas pelo driver, quando disponíveis.
- Variação das métricas durante jogos.
- Relação entre carga gráfica e frametime.
- Indícios de gargalo gráfico.

Diferenciar limitações de CPU e GPU usando múltiplos indicadores, não apenas porcentagem de utilização.

Exibir limitações da telemetria: algumas métricas podem não estar disponíveis ou representar médias temporais, e não valores instantâneos.

Não recomendar overclock automaticamente.

Não alterar clocks, voltagens ou limites de potência como parte das otimizações normais.

---

### 9. MÓDULO DE MEMÓRIA RAM

Investigar:

- Memória física total.
- Memória disponível.
- Memória comprometida.
- Commit limit.
- Consumo por processos.
- Paginação.
- Hard faults, com interpretação contextual.
- Atividade do arquivo de paginação.
- Pressão de memória.
- Potenciais relações entre memória e stuttering.

Verificar módulos e frequência real quando possível.

Considerar que o computador possui dois módulos DDR4 de 8 GB.

A memória é anunciada como 3600 MT/s, mas opera a 2933 MT/s devido à plataforma B460.

Não recomendar ativação de frequências incompatíveis.

Não recomendar desativação indiscriminada do arquivo de paginação.

Não implementar falsos otimizadores ou mecanismos de "limpeza de RAM" que apenas forcem o Windows a liberar memória sem comprovar benefício.

---

### 10. MÓDULO DOS DOIS SSDs NVMe

Implementar análise individual de cada unidade física.

Investigar:

- Modelo.
- Firmware.
- Capacidade.
- Espaço livre.
- Temperatura disponível.
- Informações SMART/NVMe disponíveis.
- Desgaste e vida útil reportada.
- Erros relevantes.
- Leitura e escrita.
- Atividade de disco.
- Latência de I/O.
- Profundidade de fila, quando possível.
- Picos de acesso.
- Possível throttling térmico, somente com evidências suficientes.

Relacionar corretamente discos físicos, partições, volumes e letras de unidade.

Identificar onde estão o Windows, o jogo e o arquivo de paginação.

Não confundir latência instantânea medida durante atividade com especificação de desempenho nominal do dispositivo.

Se o driver não disponibilizar algum contador SMART, apresentar a informação como indisponível.

Não realizar benchmarks de gravação automaticamente.

Qualquer teste que escreva dados deverá depender de autorização explícita, espaço livre suficiente e mecanismo de limpeza dos arquivos temporários criados.

---

### 11. MÓDULO DPC/ISR — PRIORIDADE MÁXIMA

Este será um dos módulos mais importantes do programa.

Implementar investigação profunda de Deferred Procedure Calls (DPC) e Interrupt Service Routines (ISR).

Utilizar ETW e ferramentas apropriadas do Windows.

Investigar, quando os eventos necessários estiverem disponíveis:

- Duração de eventos DPC.
- Duração de eventos ISR.
- Ocorrências por período.
- Distribuição das durações.
- Picos relevantes.
- Drivers e módulos envolvidos.
- Processamento por CPU lógico.
- Interrupções recorrentes.
- Correlação temporal com eventos do sistema.
- Possível relação com períodos de instabilidade de frametime.

Utilizar métodos apropriados de rastreamento de kernel.

Considerar integração com Windows Performance Recorder e bibliotecas compatíveis com leitura de ETW.

Não substituir essa análise apenas por porcentagem de utilização de CPU.

Não confundir latência DPC com input lag físico.

Apresentar informações do driver relacionado sempre que a identificação for tecnicamente possível.

Fornecer:

- Linha do tempo de eventos.
- Tabelas de drivers.
- Durações máximas observadas.
- Percentis.
- Frequência dos eventos.
- Janelas de investigação.
- Correlação com outras métricas.

Não utilizar um limite universal arbitrário como prova de defeito.

Um evento isolado não deverá automaticamente gerar alerta crítico.

**Importante:** a captura profunda de kernel deverá começar somente após comando explícito do usuário.

Gerenciar permissões, sessões ETW, interrupção da captura e limpeza dos arquivos temporários.

Se alguma análise depender de ferramentas adicionais, detectar sua presença e orientar a instalação adequada.

Se a coleta detalhada não puder ser implementada integralmente, entregar a parte funcional e marcar explicitamente os recursos pendentes, sem criar resultados simulados.

---

### 12. MÓDULO DE LATÊNCIA E RESPONSIVIDADE

Criar uma seção chamada "Latency Analysis".

Investigar possíveis fontes de atraso relacionadas a:

- Agendamento de threads.
- DPC/ISR.
- Atividade de CPU.
- Processos em segundo plano.
- Renderização.
- Apresentação de frames.
- Estabilidade de frametime.
- Rede.
- Estados de energia.
- Controladores USB, quando houver indicadores disponíveis.

Separar as informações em categorias:

**Medição direta:** valor observado por ferramenta ou API apropriada.

**Medição indireta ou estimativa:** indicador derivado de outras observações.

**Suspeita fundamentada:** possível causa que depende de investigação adicional.

**Não mensurável com os recursos atuais:** métrica que exige hardware especializado, instrumentação diferente ou recursos indisponíveis.

Não apresentar um número de "input lag total do PC" calculado arbitrariamente.

A medição exata do tempo físico clique-para-fóton depende de metodologia específica.

Não inferir polling rate real do mouse apenas pela configuração do dispositivo.

O objetivo é investigar fatores que potencialmente afetam responsividade, sem prometer medições impossíveis.

---

### 13. MÓDULO DE REDE — INTEL I219-V

Criar uma seção técnica completa de diagnóstico e configuração de rede.

Inicialmente otimizar para Intel Ethernet Connection I219-V.

Utilizar ferramentas oficiais do Windows e propriedades do driver.

Detectar dinamicamente as configurações realmente suportadas.

Investigar:

- Interrupt Moderation.
- Interrupt Moderation Rate.
- Energy Efficient Ethernet.
- Receive Side Scaling (RSS).
- RSS Queues, quando disponíveis.
- Receive Buffers.
- Transmit Buffers.
- Speed & Duplex.
- Flow Control.
- Large Send Offload (LSO).
- Checksum Offload.
- Gerenciamento de energia.
- Recursos Wake-on-LAN relevantes.
- Versão do driver.
- Velocidade negociada.
- Erros e estatísticas disponíveis.

Para cada configuração, mostrar:

- Nome técnico.
- Valor atual.
- Valores válidos suportados pelo driver.
- Função.
- Consequências potenciais.
- Recomendação baseada em evidências.
- Impacto esperado.
- Riscos.
- Possível necessidade de reinicialização do adaptador.

Não presumir que todas as propriedades existem em qualquer versão do driver.

Utilizar identificadores técnicos estáveis e propriedades disponíveis no sistema, evitando depender exclusivamente de nomes traduzidos da interface.

Não adotar a regra de desabilitar todos os offloads.

Não desabilitar RSS automaticamente.

Não forçar Speed & Duplex sem evidência de problema de negociação.

Implementar testes de rede iniciados manualmente:

- Ping.
- RTT mínimo, médio, máximo e percentis.
- Jitter, indicando sua definição matemática.
- Perda de pacotes observada.
- Variação temporal.
- Teste até o gateway local.
- Testes para destinos externos selecionados.
- Teste opcional de latência sob carga.
- Investigação de mudanças entre repouso e carga.

Diferenciar problemas do computador, LAN, roteador, provedor e destinos externos, respeitando os limites das medições.

Falhas de resposta ICMP não deverão ser interpretadas automaticamente como perda real de tráfego de jogo.

Não declarar que um resultado de ping externo equivale ao ping de dentro do VALORANT.

**Otimizações de rede:**

Implementar ações controladas que possam modificar propriedades suportadas do adaptador.

Antes de executar:

1. Capturar configuração original.
2. Validar adaptador correto.
3. Validar propriedade e valor.
4. Explicar consequências.
5. Solicitar autorização.
6. Registrar a operação.
7. Aplicar alteração.
8. Verificar o resultado.
9. Permitir restauração.

A aplicação poderá interromper temporariamente a conexão.

Implementar mecanismos de recuperação e instruções locais que não dependam de internet.

Não executar resets globais de TCP/IP, Winsock ou outras alterações destrutivas sem justificativa específica.

---

### 14. MONITORAMENTO OPCIONAL DURANTE VALORANT

Implementar uma funcionalidade chamada "Game Session Analyzer".

**Esta função deve ser opcional e ficar DESATIVADA por padrão.**

O usuário deve poder:

- Ativar o monitoramento.
- Desativar o monitoramento.
- Iniciar uma sessão.
- Encerrar uma sessão.
- Escolher o processo monitorado.
- Ajustar a intensidade da coleta.
- Consultar o relatório da sessão.

Utilizar métodos externos e não invasivos.

Investigar:

- FPS apresentado, quando disponível.
- Frametimes por frame.
- Estabilidade dos frametimes.
- Percentis de frametime.
- Métricas 1% low e 0,1% low com metodologia explicitada.
- Stuttering.
- Picos de CPU.
- Utilização de GPU.
- Pressão de memória.
- Atividade de disco.
- Eventos DPC/ISR, quando uma captura compatível tiver sido iniciada.
- Correlações temporais entre indicadores.

Considerar integração com PresentMon.

Verificar a versão instalada e os campos efetivamente disponíveis.

Não tratar dados inexistentes como zero.

Não assumir compatibilidade garantida com todas as versões do VALORANT.

Respeitar integralmente as restrições do Riot Vanguard.

**Proibições:**

- Não injetar DLL no processo do jogo.
- Não ler ou modificar memória protegida do jogo.
- Não manipular arquivos ou processos do anticheat.
- Não instalar drivers para contornar proteções.
- Não utilizar hooks invasivos.
- Não tentar mascarar a existência do aplicativo.
- Não desabilitar o Vanguard.

O monitoramento deverá preferencialmente funcionar sem overlay dentro do jogo.

Uma janela externa ou um relatório após a sessão é suficiente.

Se algum recurso causar incompatibilidade com Vanguard, desabilitá-lo e informar a limitação.

O programa deverá registrar seu próprio overhead de monitoramento sempre que possível, evitando que a coleta prejudique significativamente a sessão.

---

### 15. DIAGNÓSTICO DO WINDOWS E PROCESSOS

Criar um módulo voltado especificamente para fatores de desempenho do sistema operacional.

Investigar:

- Processos com consumo elevado de CPU.
- Uso de memória em segundo plano.
- Programas de inicialização.
- Serviços com atividade significativa.
- Tarefas de fundo.
- Atividade inesperada de disco.
- Eventos relevantes de estabilidade.
- Drivers relacionados a possíveis problemas de performance.
- Plano de energia ativo.
- Configurações de gerenciamento de energia relevantes.
- Atividade de processos durante jogos.

Não listar serviços como problemáticos apenas porque estão ativos.

Não recomendar encerrar ou desabilitar um processo sem analisar sua função e impacto.

Não desativar serviços essenciais do Windows.

O aplicativo não precisa implementar auditoria de segurança, procura de malware nem gerenciamento de atualizações do Windows.

**O foco é exclusivamente desempenho, estabilidade operacional e latência.**

---

### 16. SISTEMA INTELIGENTE DE RECOMENDAÇÕES

Desenvolver um motor de diagnóstico baseado em regras, medições e correlação temporal.

Não utilizar apenas listas fixas de configurações supostamente ideais.

Cada recomendação deverá conter:

- Identificador.
- Componente afetado.
- Descrição do problema.
- Evidências coletadas.
- Fonte das medições.
- Contexto de execução.
- Gravidade.
- Nível de confiança.
- Impacto esperado.
- Risco da alteração.
- Requisitos.
- Método de verificação.
- Método de reversão.
- Estado da recomendação.

Categorias:

**Problema comprovado:** evidência suficiente para afirmar que a anomalia observada existe.

**Possível problema:** indícios que exigem investigação.

**Oportunidade de otimização:** alteração com benefício plausível, mas ainda não validado neste computador.

**Sem anomalia detectada:** dados disponíveis não mostram problema nas condições observadas.

**Dados insuficientes:** não foi possível chegar a uma conclusão confiável.

O aplicativo deverá explicar suas conclusões em português claro, mas mantendo terminologia técnica quando necessária.

Não gerar percentuais fictícios de melhoria.

Não classificar como problema uma configuração apenas porque ela difere da preferência de alguma comunidade de otimização.

Uma configuração padrão estável pode ser a melhor opção.

---

### 17. MOTOR DE OTIMIZAÇÕES AUTOMÁTICAS

Desenvolver um mecanismo de otimizações automatizadas, mas controladas.

O programa deverá permitir selecionar uma ou várias otimizações e aplicá-las por meio de um botão.

Entretanto, nenhuma alteração deverá ocorrer sem autorização explícita do usuário.

Criar três categorias de ações:

**Baixo risco:**
Alterações reversíveis, documentadas e com impacto operacional limitado.

**Risco moderado:**
Alterações que podem exigir reinicialização, interromper temporariamente serviços ou conexão, ou causar regressões.

**Alto risco:**
Mudanças com consequências relevantes, difícil reversão ou evidência insuficiente de benefício.

Ações de alto risco deverão ficar fora da otimização automática por padrão.

O motor deverá oferecer:

- Pré-visualização das mudanças.
- Validação de pré-requisitos.
- Backup dos valores originais.
- Execução individual.
- Execução em lote quando segura.
- Histórico.
- Verificação pós-aplicação.
- Tratamento de falhas.
- Restauração.
- Comparação de resultados.

Cada otimização deverá ser representada por uma operação estruturada, com validação, execução e reversão apropriadas.

Não construir o mecanismo com comandos PowerShell genéricos enviados diretamente da interface.

Usar ações tipadas e parâmetros validados.

Caso uma operação não seja completamente reversível, informar isso antes de permitir a execução e restringir sua automação.

Não prometer que a criação de um ponto de restauração garante reversão integral de todas as configurações.

**Não aplicar automaticamente:**

- Hacks genéricos de Registro.
- Alterações de HPET sem metodologia adequada.
- Modificações arbitrárias de BCD.
- Desativação de proteções do Windows.
- Desativação indiscriminada de serviços.
- Desativação do arquivo de paginação.
- Alterações de BIOS.
- Overclock.
- Manipulações não documentadas de drivers.
- Tweaks que dependem apenas de recomendações informais.
- Mudanças de prioridade para tempo real indiscriminadamente.

O aplicativo deverá priorizar o desempenho mensurável e a estabilidade.

---

### 18. SISTEMA DE TESTES CONTROLADOS

Criar uma seção "Test Lab".

**REGRA OBRIGATÓRIA: nenhum benchmark, teste de estresse ou teste ativo de rede deverá começar automaticamente.**

Todos devem depender de um clique explícito.

Implementar, de forma progressiva:

**CPU:**
- Carga controlada.
- Single-thread, quando viável.
- Multithread.
- Duração configurável.
- Monitoramento de temperaturas e frequências.

**RAM:**
- Testes controlados de alocação e acesso.
- Avaliação de comportamento sob pressão.
- Respeito aos limites de memória disponível.

Não declarar que testes superficiais comprovam ausência de erros físicos na RAM.

**NVMe:**
- Teste de leitura.
- Teste opcional de escrita.
- Medição de throughput e latência.
- Comparação entre as unidades.
- Controle de tamanho do teste.
- Limpeza segura de arquivos temporários.

**GPU:**
- Testes compatíveis com a API gráfica.
- Monitoramento térmico e de utilização.
- Implementação apenas quando houver método real e verificável.

**Rede:**
- Testes de latência.
- Perda.
- Jitter.
- Comparação com e sem carga.
- Testes de configurações, quando apropriado.

Todos os testes deverão apresentar:

- Duração.
- Objetivo.
- Recursos consumidos.
- Possíveis riscos.
- Condições de interrupção.
- Progresso.
- Botão de cancelamento.
- Resultados.

Aplicar limites conservadores para evitar carga excessiva ou consumo desnecessário de armazenamento.

Interromper ou permitir interrupção imediata quando houver condições inadequadas.

---

### 19. COMPARAÇÃO ANTES E DEPOIS

Implementar uma funcionalidade chamada "Performance Comparison".

O usuário deverá poder registrar um diagnóstico antes da otimização e outro após ela.

Comparar:

- Métricas observadas.
- Comportamento da CPU.
- GPU.
- Memória.
- Armazenamento.
- Rede.
- DPC/ISR.
- Frametime.
- Indicadores de estabilidade.

A comparação deverá informar diferenças nas condições de teste.

Exemplo:

Se um teste foi executado com cargas de trabalho diferentes, avisar que os resultados não são diretamente comparáveis.

Quando possível, repetir testes para reduzir a influência de variabilidade.

Não atribuir automaticamente uma melhoria a determinada otimização apenas porque dois valores são diferentes.

Utilizar métodos estatísticos adequados, incluindo percentis e informações sobre número de amostras.

Permitir registrar mudanças aplicadas entre os testes.

---

### 20. HISTÓRICO E RELATÓRIOS

Persistir os dados localmente.

Utilizar banco de dados adequado, preferencialmente SQLite.

Armazenar:

- Sessões de diagnóstico.
- Data e duração.
- Configuração detectada.
- Métricas.
- Problemas identificados.
- Recomendações.
- Otimizações aplicadas.
- Valores anteriores.
- Resultados posteriores.
- Estado de reversão.

Permitir exportação para JSON e CSV.

Adicionar relatório HTML local, se viável.

O relatório deverá incluir:

1. Resumo do computador.
2. Condições da análise.
3. Principais descobertas.
4. Evidências.
5. Métricas técnicas.
6. Limitações de coleta.
7. Recomendações priorizadas.
8. Histórico de alterações.
9. Resultados comparativos.

Evitar exportar endereços IP, identificadores de hardware sensíveis ou caminhos pessoais desnecessariamente. Oferecer opção de anonimização.

O aplicativo deve funcionar localmente e não exigir conta ou serviço em nuvem.

---

### 21. PRECISÃO, VALIDAÇÃO E QUALIDADE DAS MEDIÇÕES

Esta seção é obrigatória.

Cada métrica deverá possuir metadados técnicos:

- Nome.
- Unidade.
- Origem.
- Método de coleta.
- Timestamp.
- Intervalo de amostragem.
- Disponibilidade.
- Precisão ou limitação conhecida.
- Se é instantânea, média ou valor acumulado.
- Se é medida diretamente ou derivada.

Padronizar os timestamps para permitir correlação temporal.

Separar dados brutos e dados processados.

Não apresentar métricas sintéticas como se fossem capturadas diretamente do hardware.

Tratar corretamente erros de sensor, permissões insuficientes, contadores indisponíveis e drivers incompatíveis.

A ausência de uma métrica não deverá derrubar todo o aplicativo.

Não inferir causalidade exclusivamente de correlação temporal.

Exibir estados explícitos como:

- Disponível.
- Coletando.
- Sem suporte.
- Permissão necessária.
- Ferramenta auxiliar ausente.
- Falha de leitura.
- Dados insuficientes.

**PROIBIDO MOSTRAR DADOS FICTÍCIOS COMO RESULTADOS REAIS.**

Dados simulados poderão existir exclusivamente em testes automatizados ou modo de demonstração claramente identificado.

---

### 22. DESEMPENHO DO PRÓPRIO APLICATIVO

O PC Performance Analyzer não pode se tornar uma fonte significativa de perda de desempenho.

Implementar:

- Amostragem adaptativa.
- Intervalos de coleta configuráveis.
- Atualização de interface desacoplada da coleta.
- Redução de atividade quando minimizado.
- Limites para buffers.
- Descarte ou agregação controlada de amostras antigas.
- Escrita em lote no banco de dados.
- Limites para tamanho dos logs.
- Limites para arquivos ETW.
- Monitoramento do consumo do próprio aplicativo.

Criar modos de intensidade de coleta:

**Leve:** métricas essenciais, baixo overhead.

**Avançado:** maior detalhamento, quando solicitado.

**Profundo:** rastreamento intensivo por tempo limitado e acionamento manual.

O monitoramento em jogos deverá ter um perfil de coleta leve e configurável.

Não manter sessões profundas de ETW permanentemente ativas.

Ao fechar o programa, encerrar de forma adequada as sessões e processos auxiliares iniciados por ele.

---

### 23. PERMISSÕES E OPERAÇÕES PRIVILEGIADAS

Não executar todo o aplicativo como administrador por padrão se isso puder ser evitado.

Executar a interface com privilégios normais.

Solicitar elevação apenas quando uma operação exigir.

Separar operações privilegiadas por interfaces específicas.

Não aceitar comandos arbitrários de entrada como instruções de sistema.

Validar os parâmetros de alterações de rede, energia e outras configurações.

Antes de qualquer alteração:

- Confirmar o componente correto.
- Verificar o estado atual.
- Registrar o valor original.
- Verificar o valor de destino.
- Confirmar permissão.
- Registrar o resultado.

Manter logs úteis para investigação de falhas.

A segurança operacional do programa deverá ser preservada, mesmo sem implementar um módulo de auditoria de segurança do Windows.

---

### 24. TRATAMENTO DE FALHAS E REVERSÃO

O programa deverá ser resistente a:

- Sensores indisponíveis.
- Falhas de WMI.
- Ausência de permissões.
- Driver incompatível.
- Ferramentas auxiliares ausentes.
- Desconexão de rede.
- Encerramento inesperado.
- Falha durante otimização.
- Sessões ETW interrompidas.
- Falta de espaço em disco.
- Problemas de acesso ao banco de dados.

Implementar logs estruturados.

O mecanismo de restauração deverá persistir as informações necessárias antes de aplicar a mudança.

Reverificar o estado real após tentativas de restauração.

Se a reversão falhar, não informar que houve sucesso.

Mostrar instruções técnicas para recuperação manual quando necessário.

---

### 25. DOCUMENTAÇÃO TÉCNICA DE REFERÊNCIA

Consulte prioritariamente as fontes oficiais e a documentação original dos projetos utilizados.

Windows Performance Toolkit:
https://learn.microsoft.com/en-us/windows-hardware/test/wpt/

Microsoft — CPU Analysis:
https://learn.microsoft.com/en-us/windows-hardware/test/wpt/cpu-analysis

Intel PresentMon:
https://github.com/GameTechDev/PresentMon

PresentMon Console:
https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md

LibreHardwareMonitor:
https://github.com/LibreHardwareMonitor/LibreHardwareMonitor

NVIDIA NVML:
https://docs.nvidia.com/deploy/nvml-api/latest/

Microsoft — Get-NetAdapterAdvancedProperty:
https://learn.microsoft.com/en-us/powershell/module/netadapter/get-netadapteradvancedproperty

Microsoft — Set-NetAdapterAdvancedProperty:
https://learn.microsoft.com/en-us/powershell/module/netadapter/set-netadapteradvancedproperty

Intel — Advanced Ethernet Settings:
https://www.intel.com/content/www/us/en/support/articles/000005593/ethernet-products.html

Microsoft — Storage Reliability Counter:
https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-storagereliabilitycounter

Riot Games — Vanguard FAQ:
https://www.riotgames.com/en/DevRel/vanguard-faq

Antes de implementar cada integração, confira documentação, versão, disponibilidade, licença e compatibilidade.

Não invente APIs, métodos ou nomes de propriedades.

Não assuma que uma funcionalidade documentada para Windows Server está automaticamente disponível em qualquer instalação de Windows 10.

---

### 26. PLANO DE IMPLEMENTAÇÃO

O projeto é extenso. Não tente criar dezenas de telas vazias e chamar isso de aplicativo concluído.

Implemente em etapas, entregando funcionalidades reais e verificáveis.

**ETAPA 1 — Fundação funcional**

- Criar a solução.
- Definir arquitetura.
- Configurar WPF/MVVM.
- Implementar descoberta de hardware.
- Criar dashboard técnico.
- Implementar persistência básica.
- Implementar logging.
- Validar build.

**ETAPA 2 — Telemetria**

- Coleta real de CPU.
- Coleta real de GPU.
- Coleta real de RAM.
- Coleta individual dos NVMe.
- Gráficos temporais.
- Tratamento de sensores indisponíveis.
- Histórico de métricas.

**ETAPA 3 — Diagnóstico profundo**

- Infraestrutura ETW.
- Captura controlada.
- Análise DPC/ISR.
- Identificação de drivers quando possível.
- Correlação temporal.
- Interface técnica de investigação.

**ETAPA 4 — Rede**

- Identificação Intel I219-V.
- Leitura das propriedades suportadas.
- Exibição dos valores válidos.
- Testes manuais de rede.
- Recomendações.
- Sistema de backup e alteração controlada.
- Restauração e validação.

**ETAPA 5 — Jogos**

- Integração compatível com PresentMon.
- Detecção ou seleção de processo.
- Monitoramento opcional.
- Captura de frametimes.
- Cálculo de métricas.
- Relatórios.
- Correlação com telemetria do sistema.
- Verificação de compatibilidade com Vanguard.

**ETAPA 6 — Diagnóstico inteligente**

- Motor de regras.
- Classificação de problemas.
- Evidências.
- Priorização.
- Níveis de confiança.
- Explicações técnicas.
- Recomendações.

**ETAPA 7 — Otimizações**

- Catálogo de operações suportadas.
- Pré-visualização.
- Permissões.
- Backup.
- Aplicação.
- Verificação.
- Restauração.
- Histórico.

**ETAPA 8 — Testes e comparação**

- Testes controlados.
- Botões manuais de execução.
- Limites de segurança operacional.
- Cancelamento.
- Comparação de sessões.
- Análise estatística.
- Relatórios.

**ETAPA 9 — Finalização**

- Refinar interface.
- Corrigir bugs.
- Melhorar tratamento de erros.
- Reduzir overhead.
- Criar instalador ou publicação executável.
- Documentar instalação.
- Documentar limitações.
- Validar funcionamento.

Não permaneça indefinidamente na fase de planejamento.

Depois de definir a arquitetura, comece a criar e implementar o projeto.

Se houver limitações de tempo ou ferramentas, priorize uma versão funcional de ponta a ponta, seguida de melhorias incrementais.

---

### 27. CRITÉRIOS DE ACEITAÇÃO

O aplicativo somente poderá ser considerado funcional quando demonstrar, no mínimo:

1. Inicialização em Windows 10 x64 compatível.
2. Detecção real do computador.
3. Identificação correta de CPU, GPU, RAM e unidades NVMe.
4. Leitura real de indicadores essenciais de CPU, GPU e memória.
5. Gráficos atualizados com informações reais.
6. Identificação do adaptador de rede e de propriedades disponíveis.
7. Execução manual de teste de rede.
8. Persistência de uma sessão de diagnóstico.
9. Exibição de recomendações com evidências e níveis de confiança.
10. Tratamento correto de informações indisponíveis.
11. Demonstração funcional de uma operação reversível de otimização, quando houver operação segura e suportada.
12. Ausência de benchmarks automáticos na abertura.
13. Interface responsiva durante coletas.
14. Documentação dos recursos ainda não implementados.

Para os módulos avançados, incluir testes específicos.

**ETW:** demonstrar que os eventos realmente foram coletados e processados.

**PresentMon:** demonstrar leitura válida de uma captura real de aplicação gráfica compatível.

**Rede:** demonstrar leitura, alteração autorizada de propriedade suportada, validação e restauração.

**Relatórios:** demonstrar que os valores exportados correspondem aos dados armazenados.

Não substituir essas verificações por testes que simplesmente confirmam a existência das classes.

---

### 28. TESTES AUTOMATIZADOS DO SOFTWARE

Criar testes para:

- Modelos.
- Regras de diagnóstico.
- Cálculo de métricas.
- Percentis de frametime.
- Cálculo de jitter.
- Conversão de unidades.
- Tratamento de valores ausentes.
- Comparação de sessões.
- Persistência.
- Serialização.
- Validação de otimizações.
- Mecanismo de reversão.
- Falhas simuladas de coletores.

Utilizar interfaces e mocks para isolar dependências de hardware nos testes unitários.

Os testes de integração dependentes de hardware real deverão estar separados.

Não executar mudanças reais de sistema durante a suíte normal de testes.

Se o ambiente atual não for Windows, não declare que testou WPF, ETW, drivers e sensores em hardware real.

Informe quais testes foram realizados e quais dependem de validação no Windows.

---

### 29. ENTREGA E EXECUÇÃO

Ao desenvolver:

1. Inspecione o diretório atual.
2. Verifique se existe um projeto anterior.
3. Se existir, preserve o trabalho válido.
4. Defina a estrutura de solução.
5. Implemente diretamente no repositório.
6. Restaure as dependências.
7. Compile o projeto quando o ambiente permitir.
8. Execute os testes disponíveis.
9. Corrija erros identificados.
10. Documente os resultados.

Crie um `README.md` completo explicando:

- Objetivo.
- Requisitos.
- Instalação.
- Compilação.
- Execução.
- Permissões.
- Dependências auxiliares.
- Funcionalidades implementadas.
- Funcionalidades pendentes.
- Limitações.
- Procedimentos de restauração.

Prepare publicação para Windows x64 quando tecnicamente possível.

Não considere uma compilação bem-sucedida como prova de que as métricas estão corretas.

Não declare funcionalidades concluídas sem a respectiva implementação.

Mantenha uma lista objetiva de progresso por módulo.

---

### 30. PRINCÍPIO FINAL DO PROJETO

Este aplicativo não deverá ser mais um "PC Optimizer" genérico.

Não quero um programa que exiba 50 supostos problemas, prometa aumentar FPS em 300% ou recomende centenas de modificações sem fundamentação.

Quero uma **FERRAMENTA DE ENGENHARIA E INVESTIGAÇÃO DE PERFORMANCE**.

Seu ciclo principal deverá ser:

**COLETAR DADOS → ANALISAR → IDENTIFICAR POSSÍVEIS CAUSAS → APRESENTAR EVIDÊNCIAS → RECOMENDAR → OTIMIZAR COM AUTORIZAÇÃO → MEDIR NOVAMENTE → COMPARAR RESULTADOS.**

Se o computador estiver funcionando corretamente, o aplicativo deverá ter capacidade de concluir que não existe motivo suficiente para modificar determinada configuração.

Se uma alteração não produzir benefício, isso deverá aparecer claramente.

Se uma medição não puder ser realizada, não invente resultados.

A qualidade do projeto será avaliada pela precisão dos diagnósticos, funcionamento das integrações, estabilidade, capacidade de explicação e confiabilidade das otimizações.

**INSTRUÇÃO FINAL:**

Comece agora a implementar o projeto de verdade.

Primeiro, faça uma análise técnica breve da arquitetura e dos riscos de implementação. Em seguida, crie a solução e avance pelas etapas, priorizando funcionalidades reais.

Não entregue somente documentação, mockups, pseudocódigo ou classes vazias.

Quero código executável, uma interface funcional, mecanismos de coleta reais e evolução progressiva até completar o aplicativo especificado.

Ao terminar cada etapa, informe exatamente o que funciona, o que foi testado, o que ainda está pendente e qual é a próxima implementação prioritária.