using System.Collections.ObjectModel;
using PCAnalyzer.Core;
using PCAnalyzer.Network;

namespace PCAnalyzer.App.ViewModels;

public sealed record NetworkAdapterRow(NetworkAdapter Adapter)
{
    public Guid Id => Adapter.InterfaceGuid;
    public string Name => Adapter.Name + " · " + Adapter.Description;
    public string State => Adapter.Status == "Up" ? "Conectado" : "Sem conexão ativa (" + Adapter.Status + ")";
    public string Link => Adapter.SpeedBitsPerSecond is > 0 ? $"{Adapter.SpeedBitsPerSecond / 1_000_000d:0.###} Mbit/s" : "Dados não disponíveis";
    public string Gateway => Adapter.Gateways.Count > 0 ? string.Join(", ", Adapter.Gateways) : "Sem gateway reportado";
}

public sealed partial class MainViewModel
{
    private NetworkAdapterRow? _selectedAdapter;
    private string _networkReadStatus = "Clique em Detectar conexões. Depois escolha o adaptador e leia os detalhes.";
    private string _networkDriver = "Driver ainda não consultado.";
    public ObservableCollection<NetworkAdapterRow> NetworkAdapters { get; } = [];
    public ObservableCollection<HardwareRow> NetworkFacts { get; } = [];
    public NetworkAdapterRow? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (!Set(ref _selectedAdapter, value)) return;
            NetworkFacts.Clear(); NetworkSettings.Clear(); SelectedNetworkSetting = null;
            NetworkDriver = "Clique em Ler detalhes do adaptador para consultar o driver e as propriedades.";
            InspectNetworkCommand?.Refresh(); Notify(nameof(NetworkSelectionNote));
        }
    }
    public string NetworkReadStatus { get => _networkReadStatus; private set => Set(ref _networkReadStatus, value); }
    public string NetworkDriver { get => _networkDriver; private set => Set(ref _networkDriver, value); }
    public string NetworkSelectionNote => SelectedAdapter is { } a ? $"{a.State} · link {a.Link} · gateway {a.Gateway}" : "Nenhum adaptador selecionado.";
    public string NetworkSettingState => SelectedNetworkSetting?.Status ?? "Selecione uma propriedade para entender sua função.";
    public AsyncCommand DiscoverNetworkCommand { get; private set; } = null!;
    public AsyncCommand InspectNetworkCommand { get; private set; } = null!;
    public RelayCommand ReviewNetworkSettingCommand { get; private set; } = null!;
    private void InitializeNetworkCommands()
    {
        DiscoverNetworkCommand = Command(DiscoverNetworkAsync);
        InspectNetworkCommand = Command(InspectNetworkAsync, () => SelectedAdapter is not null);
        ReviewNetworkSettingCommand = new RelayCommand(() => Navigate("ajustes"));
    }
    private async Task DiscoverNetworkAsync()
    {
        NetworkReadStatus = "Detectando conexões locais…";
        try
        {
            var snapshot = await _backend.DiscoverNetworkAsync(OperationToken);
            var previous = SelectedAdapter?.Id;
            Replace(NetworkAdapters, snapshot.Adapters.OrderByDescending(a => a.Status == "Up").ThenBy(a => a.Name).Select(a => new NetworkAdapterRow(a)));
            SelectedAdapter = NetworkAdapters.FirstOrDefault(a => a.Id == previous)
                ?? NetworkAdapters.FirstOrDefault(a => a.Adapter.Status == "Up" && a.Adapter.Gateways.Count > 0) ?? NetworkAdapters.FirstOrDefault();
            NetworkReadStatus = NetworkAdapters.Count > 0 ? $"{NetworkAdapters.Count} conexão(ões). Selecione a que deseja investigar. Nenhuma configuração foi alterada." :
                "Nenhum adaptador com identificação compatível foi encontrado. " + snapshot.Limitation;
        }
        catch (Exception ex) { NetworkReadStatus = "Não foi possível detectar conexões: " + ex.Message; throw; }
    }
    private async Task InspectNetworkAsync()
    {
        if (SelectedAdapter is not { } adapter) return;
        NetworkReadStatus = "Lendo driver, RSS, energia e propriedades do adaptador escolhido…";
        try
        {
            var detail = await _backend.InspectNetworkAsync(adapter.Id, OperationToken);
            // A user can change the selection while a read is in progress; never attach stale settings to another adapter.
            if (SelectedAdapter?.Id != adapter.Id) { NetworkReadStatus = "Seleção alterada. Leia os detalhes do novo adaptador."; return; }
            NetworkDriver = $"{detail.Description}\nDriver: {detail.DriverVersion ?? "indisponível"} · {detail.DriverInformation ?? "sem informações adicionais"}\nLink reportado: {detail.LinkSpeed ?? adapter.Link}";
            Replace(NetworkFacts, detail.Rss.Concat(detail.PowerManagement ?? []).Concat(detail.Statistics)
                .Select(f => new HardwareRow(adapter.Adapter.Name, NetworkFactName(f.Name), string.IsNullOrWhiteSpace(f.Value) ? "—" : f.Value, f.Source, AvailabilityLabel(f.Status), f.Limitation)));
            Replace(NetworkSettings, _backend.NetworkSettingsFor(detail));
            SelectedNetworkSetting = NetworkSettings.FirstOrDefault();
            NetworkReadStatus = detail.Status == Availability.Available
                ? $"Leitura concluída · {NetworkSettings.Count} propriedades. " + (detail.Limitations.Count == 0 ? "Nenhuma alteração aplicada." : "Algumas fontes faltam: " + string.Join("; ", detail.Limitations))
                : AvailabilityLabel(detail.Status) + ": " + string.Join("; ", detail.Limitations);
            Notification = NetworkReadStatus;
        }
        catch (Exception ex) { NetworkReadStatus = "Falha ao ler este adaptador: " + ex.Message; throw; }
    }
    private static string NetworkFactName(string name) => name switch
    {
        "Enabled" => "RSS ativo (distribuição da recepção entre CPUs)", "Profile" => "Perfil RSS", "NumberOfReceiveQueues" => "Filas de recepção RSS",
        "BaseProcessorNumber" => "Primeiro processador RSS", "MaxProcessorNumber" => "Último processador RSS", "MaxProcessors" => "Máximo de processadores RSS",
        "ReceivedBytes" => "Bytes recebidos (acumulados)", "SentBytes" => "Bytes enviados (acumulados)", "ReceivedPacketErrors" => "Erros de recebimento (acumulados)",
        "OutboundPacketErrors" => "Erros de envio (acumulados)", "ReceivedDiscardedPackets" => "Pacotes recebidos descartados", "OutboundDiscardedPackets" => "Pacotes de envio descartados",
        "AllowComputerToTurnOffDevice" => "Windows pode desligar o dispositivo", "DeviceSleepOnDisconnect" => "Suspender quando desconectado",
        "SelectiveSuspend" => "Suspensão seletiva", "WakeOnMagicPacket" => "Acordar por Magic Packet", "WakeOnPattern" => "Acordar por padrão de rede",
        "ArpOffload" => "Resposta ARP em baixo consumo", "NSOffload" => "Resposta IPv6 em baixo consumo", _ => name
    };
}
