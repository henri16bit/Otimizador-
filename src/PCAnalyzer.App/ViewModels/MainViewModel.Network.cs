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
    private string _networkReadStatus = "A conexão ativa será identificada ao abrir esta tela.";
    private string _networkDriver = "Driver ainda não consultado.";
    private Task? _automaticNetworkTask;
    private bool _networkDiscovered, _selectingNetwork, _automaticNetworkInProgress;
    private Guid? _lastAutomaticAdapter;
    private bool _showAllNetworkAdapters;
    public ObservableCollection<NetworkAdapterRow> NetworkAdapters { get; } = [];
    public bool ShowAllNetworkAdapters { get => _showAllNetworkAdapters; set { if (Set(ref _showAllNetworkAdapters, value)) RefreshAdapterVisibility(); } }
    public IEnumerable<NetworkAdapterRow> VisibleNetworkAdapters => ShowAllNetworkAdapters ? NetworkAdapters : NetworkAdapters.Where(a => !KnownVirtualAdapter(a));
    public string CompactNetworkReadStatus => _automaticNetworkInProgress ? "Consultando o driver…" :
        SelectedAdapter is null ? "Nenhuma conexão selecionada." : NetworkFacts.Count == 0 && NetworkSettings.Count == 0 ?
        "Leitura ainda indisponível. Consulte os detalhes ou recarregue." : "Configurações disponíveis para consulta; detalhes e limites abaixo.";
    private bool KnownVirtualAdapter(NetworkAdapterRow adapter) => adapter.Adapter.Type is "Loopback" or "Tunnel" ||
        _auditSettings.SelectMany(r => r.Evidence).Any(a => Guid.TryParse(a.Observation.Facts.GetValueOrDefault("InterfaceGuid"), out var id) && id == adapter.Id &&
            bool.TryParse(a.Observation.Facts.GetValueOrDefault("HardwareInterface"), out var hardware) && !hardware);
    private void RefreshAdapterVisibility()
    {
        Notify(nameof(VisibleNetworkAdapters));
        if (!ShowAllNetworkAdapters && SelectedAdapter is { } selected && KnownVirtualAdapter(selected))
            SelectedAdapter = VisibleNetworkAdapters.FirstOrDefault(a => a.Adapter.Status == "Up" && a.Adapter.Gateways.Count > 0) ?? VisibleNetworkAdapters.FirstOrDefault();
        RefreshCategoryGroups(); FilterAuditSettings();
    }
    public ObservableCollection<HardwareRow> NetworkFacts { get; } = [];
    public NetworkAdapterRow? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (!Set(ref _selectedAdapter, value)) return;
            NetworkFacts.Clear(); NetworkSettings.Clear(); SelectedNetworkSetting = null;
            NetworkDriver = "Aguardando a leitura do adaptador escolhido…";
            InspectNetworkCommand?.Refresh(); Notify(nameof(NetworkSelectionNote));
            RefreshCategoryGroups(); FilterAuditSettings();
            Notify(nameof(CompactNetworkReadStatus));
            if (!_selectingNetwork) EnsureNetworkLoaded();
        }
    }
    public string NetworkReadStatus { get => _networkReadStatus; private set { Set(ref _networkReadStatus, value); Notify(nameof(CompactNetworkReadStatus)); } }
    public string NetworkDriver { get => _networkDriver; private set => Set(ref _networkDriver, value); }
    public string NetworkSelectionNote => SelectedAdapter is { } a ? $"{a.State} · link {a.Link} · gateway {a.Gateway}" : "Nenhum adaptador selecionado.";
    public string NetworkSettingState => SelectedNetworkSetting?.Status ?? "Selecione uma propriedade para entender sua função.";
    public AsyncCommand DiscoverNetworkCommand { get; private set; } = null!;
    public AsyncCommand InspectNetworkCommand { get; private set; } = null!;
    public RelayCommand ReviewNetworkSettingCommand { get; private set; } = null!;
    private void InitializeNetworkCommands()
    {
        DiscoverNetworkCommand = Command(async () => { await DiscoverNetworkAsync(); await InspectNetworkAsync(); });
        InspectNetworkCommand = Command(InspectNetworkAsync, () => SelectedAdapter is not null);
        ReviewNetworkSettingCommand = new RelayCommand(() => Navigate("ajustes"));
    }
    private void EnsureNetworkLoaded()
    {
        if (_closing || _inventory is null || IsBusy || _automaticNetworkInProgress) return;
        if (_networkDiscovered && SelectedAdapter?.Id == _lastAutomaticAdapter) return;
        _automaticNetworkInProgress = true;
        _automaticNetworkTask = LoadNetworkAutomaticallyAsync();
    }
    private async Task LoadNetworkAutomaticallyAsync()
    {
        Guid? attempted = null;
        try
        {
            if (!_networkDiscovered) await DiscoverNetworkAsync();
            if (_closing) return;
            attempted = SelectedAdapter?.Id;
            _lastAutomaticAdapter = attempted;
            if (attempted is not null) await InspectNetworkAsync();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error)
        {
            _backend.LogError("network.automatic-read", error);
            // Do not erase a diagnostic report because an independent read failed.
            if (!_closing) Notification = "A leitura da rede falhou. Use Atualizar conexões para tentar novamente.";
        }
        finally
        {
            _networkDiscovered = true;
            _automaticNetworkInProgress = false;
            // A selection made while the previous read was pending is serviced after it ends.
            if (!_closing && SelectedAdapter?.Id != attempted)
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(EnsureNetworkLoaded));
        }
    }
    private async Task DiscoverNetworkAsync()
    {
        NetworkReadStatus = "Detectando conexões locais…";
        try
        {
            var snapshot = await _backend.DiscoverNetworkAsync(OperationToken);
            _networkDiscovered = true;
            var previous = SelectedAdapter?.Id;
            Replace(NetworkAdapters, snapshot.Adapters.OrderByDescending(a => a.Status == "Up").ThenBy(a => a.Name).Select(a => new NetworkAdapterRow(a)));
            Notify(nameof(VisibleNetworkAdapters));
            _selectingNetwork = true;
            try
            {
                SelectedAdapter = VisibleNetworkAdapters.FirstOrDefault(a => a.Id == previous)
                    ?? VisibleNetworkAdapters.FirstOrDefault(a => a.Adapter.Status == "Up" && a.Adapter.Gateways.Count > 0) ?? VisibleNetworkAdapters.FirstOrDefault();
            }
            finally { _selectingNetwork = false; }
            NetworkReadStatus = NetworkAdapters.Count > 0 ? $"{NetworkAdapters.Count} conexão(ões). A conexão ativa foi selecionada; você pode escolher outra. Nenhuma configuração foi alterada." :
                "Nenhum adaptador com identificação compatível foi encontrado. " + snapshot.Limitation;
        }
        catch (Exception ex)
        {
            NetworkAdapters.Clear(); SelectedAdapter = null; NetworkFacts.Clear(); NetworkSettings.Clear();
            NetworkDriver = "Sem leitura atual do driver.";
            NetworkReadStatus = "Não foi possível detectar conexões: " + ex.Message; throw;
        }
    }
    private async Task InspectNetworkAsync()
    {
        if (SelectedAdapter is not { } adapter) return;
        _lastAutomaticAdapter = adapter.Id;
        NetworkFacts.Clear(); NetworkSettings.Clear(); SelectedNetworkSetting = null;
        NetworkDriver = "Consultando o driver…";
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
            Notification = CompactNetworkReadStatus;
        }
        catch (Exception ex)
        {
            if (SelectedAdapter?.Id == adapter.Id)
            {
                NetworkFacts.Clear(); NetworkSettings.Clear(); SelectedNetworkSetting = null;
                NetworkDriver = "Não foi possível ler o driver desta conexão.";
                NetworkReadStatus = "Falha ao ler este adaptador: " + ex.Message;
            }
            throw;
        }
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
