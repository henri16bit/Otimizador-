> Atualização de 17/10/2026: a versão de consultoria é exclusivamente de leitura. Instruções antigas de aplicar/restaurar abaixo são registro histórico e não se aplicam à versão atual. Consulte [PERFORMANCE-CONSULTANCY.md](PERFORMANCE-CONSULTANCY.md) para o estado vigente.

# Rede e alterações reversíveis

## Implementado

`NetworkInspector.DiscoverAsync` enumera adaptadores reais pela API .NET. GUID, estado, tipo,
velocidade, gateways e endereços pertencem ao adaptador descoberto. Bytes e erros são contadores
acumulados do sistema: não são uma medição de throughput nem necessariamente cobrem a vida inteira
do computador; resets do driver podem zerá-los. Não se presume que o adaptador Intel I219-V exista.

`InspectAdapterAsync(Guid)` lê o adaptador selecionado no módulo NetAdapter do Windows PowerShell:
versão e informação do driver, velocidade negociada reportada, propriedades avançadas, respectivos
`RegistryKeyword`, valores atuais, `ValidRegistryValues` / `ValidDisplayValues`, RSS e estatísticas.
As propriedades são descobertas no driver instalado, sem lista fixa de valores. Estados indisponíveis
e falhas parciais são explicitados; não são substituídos por zeros ou dados simulados.
Guias informativos explicam RSS, EEE, moderação, buffers e offloads quando o identificador é conhecido.
Esses textos não demonstram benefício de modificar a propriedade.

`ManualPingService.RunAsync` executa somente por uma chamada explícita. Aceita IP ou nome DNS,
1–100 sondas ICMP, timeout de 100–5000 ms e cancelamento. O serviço não aceita URL, porta ou comando.
DNS tem limite de cinco segundos. O resultado inclui série temporal, RTT mínimo/médio/máximo,
percentil p95 (R-7), ausência de resposta e jitter. Jitter é a média de diferenças absolutas entre RTTs
sucessivos, sem construir pares através de uma resposta ausente; não é a fórmula RFC 3550.
Erros locais de envio e sondas canceladas sem resultado são excluídos do denominador de perda.
O teste ao gateway utiliza um gateway descoberto e a mesma ação manual.

ICMP pode ser filtrado, limitado ou receber prioridade inferior. Ausência de resposta ICMP não
comprova perda no jogo. RTT desse destino não é latência do servidor do VALORANT e não mede
latência física de entrada. Não há geração automática de tráfego nem teste sob carga nesta versão.

## Fronteira de permissão e parâmetros

O projeto Network não solicita elevação. O processo separado iniciado explicitamente pela interface
aplica alterações autorizadas. O script PowerShell é uma constante do código, enviada com
`-EncodedCommand`. Os parâmetros são JSON em stdin e nunca instruções interpretadas.
Há limite de execução de 30 s e limites de saída. Cancelar encerra o processo auxiliar.

O adaptador é resolvido por `InterfaceGuid`, com exigência de correspondência única. Nomes de
adaptador passam por `WildcardPattern.Escape`. As propriedades são selecionadas pela igualdade
exata de `RegistryKeyword`, e a alteração utiliza o objeto CIM exato em
`Set-NetAdapterAdvancedProperty -InputObject`: um `*` do identificador padronizado nunca é usado
como padrão de seleção de outras propriedades. O próprio script privilegiado reverifica valores
suportados pelo driver antes da escrita. Somente propriedades escalares com valores válidos
enumerados pelo driver são editáveis. Propriedades sem enumeração, ou arrays, ficam somente para
leitura; não se inventa um intervalo válido.

O valor esperado enviado ao auxiliar vem do backup durável, nunca de uma releitura tardia que
aceitaria alterações externas. O motor passa `Before` ao aplicar e `After` ao restaurar através de
`IExpectedValueOperation`. O script NetAdapter compara o valor real com esse original esperado
imediatamente antes da escrita. As APIs do Windows não oferecem compare-and-swap para essa
configuração; permanece uma pequena janela entre comparação e chamada nativa.

`NetworkPropertyOperation` utiliza valores JSON canônicos (`SerializeValue`). É classificada como
risco moderado. A escrita utiliza `-NoRestart`: confirma apenas a configuração persistida;
não comprova sua ativação pelo driver. Reinício do adaptador/computador pode ser necessário,
interromper a conexão e requer ação separada do usuário. Não há reset global de rede, alterações
genéricas de Registro, troca automática de Speed & Duplex ou desativação conjunta de offloads/RSS.

`PowerPlans` usa as APIs documentadas PowrProf `PowerEnumerate`, `PowerReadFriendlyName`,
`PowerGetActiveScheme` e `PowerSetActiveScheme`. `PowerPlanOperation` aceita apenas GUID canônico
de um plano que ainda exista no computador. Muda somente o plano ativo; não cria, apaga ou modifica
configurações internas do plano. A mudança é de risco baixo, mas consumo e temperatura podem variar.

## Diário e recuperação

`ReversibleChangeEngine` recebe `IReversibleOperation`, permitindo testes sem modificar o Windows.
O fluxo de aplicação lê e valida original/destino, persiste o registro `Prepared`, confere novamente
o estado atual, escreve, relê e persiste o resultado. A falha em salvar o original bloqueia a escrita.
O `IHistoryStore` deve concluir uma transação durável antes de retornar de `SaveChangeAsync`.
Cancelamento depois de uma escrita não elimina o backup nem interrompe a gravação do resultado.
Erros de escrita registram a falha e tentam uma leitura adicional para esclarecer o estado real.

Restauração verifica tipo e componente, relê o estado real e exige que ele seja igual ao valor
aplicado ou já igual ao original. Um terceiro valor é conflito: o sistema não sobrescreve alterações
externas. Antes de escrever o original, o motor salva `RestorePrepared`; depois relê para verificar.
Uma divergência é `RestoreFailed`, nunca sucesso. Registros incompletos preservam original e destino
para recuperação explícita, sem reexecutar alterações na abertura. Se o valor já é o original,
restaurar é idempotente e ainda verifica o estado real. Alterações sucessivas mantêm backups individuais;
desfaça a mais recente primeiro para evitar conflitos.

Um término inesperado pode deixar `Prepared` ou `RestorePrepared`. Conferir o registro no histórico,
selecionar o componente correto e usar Restaurar. O motor compara o valor real antes de escrever.
Se o driver ou plano original deixou de existir, restaurar não é garantido. A alternativa manual é
Opções de Energia para planos ou Gerenciador de Dispositivos → adaptador correto → Avançado para
propriedades. Use o valor `Before` registrado; não confunda um ponto de restauração com backup dessa
propriedade. Uma falha ao persistir o resultado é informada explicitamente, com o ID do backup.

## Validação

Os testes `OptimizationNetworkTests` usam apenas operações e armazenamento falsos identificados
como testes: backup antes da escrita, falha do diário, destino inválido, leitura divergente,
cancelamento após escrita, falha após alteração parcial, restauração com conflito, intenção de
restauração durável, recuperação de diário incompleto, backups sucessivos e estatísticas ICMP com
lacunas. Nenhum teste normal envia ping nem muda configurações reais.

Dois testes adicionais executam `NetworkPropertyOperation` com `INetworkPropertyAccess` falso:
uma mudança externa tardia é recusada sem sobrescrever o terceiro valor; a restauração envia o
valor aplicado como precondição e o original como destino.

Windows 10 Pro 22H2 x64 é o alvo informado. Linux permite compilar os projetos e validar regras
puras; isso não valida NetAdapter, UAC ou PowrProf em hardware Windows. No Windows, validar primeiro
a leitura passiva. Uma prova de alteração/restauração deve ser manual, em componente selecionado,
com valor original anotado, operação única e acesso local para recuperação. Registrar driver,
reinício necessário e condições de teste. Não atribuir mudança de desempenho à operação sem
comparações repetidas e condições equivalentes.

Pendentes: medição de rede sob carga, correlação temporal com captura ETW/frametime, descoberta
de recursos adicionais de energia do adaptador, valores de propriedades definidas somente por
intervalo e validação prática de alteração/restauração na máquina Windows alvo.

## Referências consultadas

- [Microsoft Get-NetAdapterAdvancedProperty](https://learn.microsoft.com/en-us/powershell/module/netadapter/get-netadapteradvancedproperty)
- [Microsoft Set-NetAdapterAdvancedProperty](https://learn.microsoft.com/en-us/powershell/module/netadapter/set-netadapteradvancedproperty)
- [Microsoft PowerEnumerate](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerenumerate)
- [Microsoft PowerGetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme)
- [Microsoft PowerSetActiveScheme](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersetactivescheme)
- [Microsoft PowerReadFriendlyName](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadfriendlyname)
- [Intel Advanced Ethernet Settings](https://www.intel.com/content/www/us/en/support/articles/000005593/ethernet-products.html)

NetAdapter e PowrProf são componentes do Windows; esses módulos não adicionam dependências NuGet,
drivers ou serviços externos. Disponibilidade de propriedades continua dependente da versão real do
driver e do módulo no Windows alvo, não da existência de documentação para Windows Server.
