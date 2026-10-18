using System.Diagnostics;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCAnalyzer.Core;

namespace PCAnalyzer.Network;

public sealed class NetworkInspector : INetworkPropertyAccess
{
    public Task<NetworkSnapshot> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapters = new List<NetworkAdapter>();
        var limitations = new List<string>();
        NetworkInterface[] installed;
        try { installed = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return new NetworkSnapshot(DateTimeOffset.UtcNow, [], Availability.ReadError, ex.Message);
        }
        foreach (var adapter in installed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A typed GUID is required for all NetAdapter actions; platform-specific identifiers are not accepted.
            if (!Guid.TryParse(adapter.Id, out var id)) continue;
            try
            {
                var properties = adapter.GetIPProperties();
                var counters = adapter.GetIPStatistics();
                adapters.Add(new(id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType.ToString(),
                    adapter.OperationalStatus.ToString(), adapter.Speed >= 0 ? adapter.Speed : null,
                    properties.UnicastAddresses.Select(x => x.Address.ToString()).ToArray(),
                    properties.GatewayAddresses.Select(x => x.Address.ToString()).ToArray(),
                    counters.BytesReceived, counters.BytesSent, counters.IncomingPacketsWithErrors, counters.OutgoingPacketsWithErrors));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                adapters.Add(new(id, adapter.Name, adapter.Description, adapter.NetworkInterfaceType.ToString(),
                    adapter.OperationalStatus.ToString(), null, [], [], null, null, null, null));
                limitations.Add($"{adapter.Name}: {ex.Message}");
            }
        }
        if (!OperatingSystem.IsWindows()) limitations.Add("A consulta NetAdapter exige Windows; IDs não GUID foram excluídos.");
        return new NetworkSnapshot(DateTimeOffset.UtcNow, adapters,
            OperatingSystem.IsWindows() ? Availability.Available : Availability.Unsupported,
            limitations.Count == 0 ? null : string.Join("; ", limitations));
    }, cancellationToken);

    public async Task<AdapterDetail> InspectAdapterAsync(Guid interfaceGuid, CancellationToken cancellationToken = default)
    {
        RequireGuid(interfaceGuid);
        if (!OperatingSystem.IsWindows()) return Unavailable(interfaceGuid, Availability.Unsupported, "PowerShell NetAdapter exige Windows.");
        try
        {
            using var json = await RunNetAdapterAsync(new { Action = "Inspect", InterfaceGuid = interfaceGuid }, cancellationToken);
            return ParseAdapter(interfaceGuid, json.RootElement);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or TimeoutException or Win32Exception or UnauthorizedAccessException)
        {
            return Unavailable(interfaceGuid, ErrorStatus(ex), PublicError(ex));
        }
    }

    internal static AdapterDetail ParseAdapter(Guid interfaceGuid, JsonElement root)
    {
            if (!root.TryGetProperty("Identity", out var identity) || identity.ValueKind != JsonValueKind.Object ||
                !Guid.TryParse(ReadString(identity, "InterfaceGuid"), out var actual) || actual != interfaceGuid)
                throw new InvalidOperationException("A identidade retornada não corresponde à conexão solicitada.");
            var properties = ReadArray(root, "Properties").Select(p =>
            {
                var keyword = ReadString(p, "RegistryKeyword") ?? "";
                var guidance = PropertyGuidance.For(keyword);
                var label = ReadString(p, "DisplayName");
                return new NetworkProperty(keyword, string.IsNullOrWhiteSpace(label) ? keyword : label,
                    ReadStrings(p, "RegistryValue"), ReadString(p, "DisplayValue"),
                    ReadStrings(p, "ValidRegistryValues"), ReadStrings(p, "ValidDisplayValues"),
                    guidance.Function, guidance.Consequences, guidance.Recommendation, guidance.ExpectedImpact);
            }).Where(p => p.RegistryKeyword.Length > 0).ToArray();
            var limitations = ReadStrings(root, "Limitations");
            return new(interfaceGuid, ReadString(root, "Name") ?? "", ReadString(root, "Description") ?? "",
                ReadString(root, "DriverVersion"), ReadString(root, "DriverInformation"), ReadString(root, "DriverDate"),
                ReadString(root, "LinkSpeed"), properties, ReadFacts(root, "Rss", "Get-NetAdapterRss"),
                ReadFacts(root, "Statistics", "Get-NetAdapterStatistics"), Availability.Available, limitations,
                PowerManagement: ReadFacts(root, "Power", "Get-NetAdapterPowerManagement"),
                Identity: ReadFacts(root, "Identity", "MSFT_NetAdapter / Win32_NetworkAdapter"),
                PropertiesStatus: root.TryGetProperty("Statuses", out var states) && states.ValueKind == JsonValueKind.Object &&
                    Enum.TryParse<Availability>(ReadString(states, "Properties"), out var state) ? state : Availability.Available);
    }

    private static Availability ErrorStatus(Exception error) => error switch
    {
        NetworkReadException read => read.Status,
        UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } => Availability.PermissionRequired,
        Win32Exception { NativeErrorCode: 2 } => Availability.ToolMissing,
        IOException when error.Message.Contains("Windows PowerShell não encontrado", StringComparison.Ordinal) => Availability.ToolMissing,
        _ => Availability.ReadError
    };

    private static string PublicError(Exception error)
    {
        PowerShellErrorText.Log(error.ToString());
        return error is NetworkReadException ? error.Message : error is TimeoutException
            ? "O driver demorou demais para responder. Atualize a consulta; detalhes estão no log de rede."
            : ErrorStatus(error) == Availability.PermissionRequired ? "O Windows negou acesso à consulta. Nenhuma elevação foi iniciada."
            : "Não foi possível consultar esta conexão. Atualize a lista; detalhes estão no log de rede.";
    }

    public async Task<IReadOnlyList<HardwareFact>> InspectTcpAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return [new("Perfis TCP", null, "Get-NetTCPSetting", Availability.Unsupported, "A consulta exige Windows.")];
        try
        {
            using var json = await RunNetAdapterAsync(new { Action = "Tcp" }, cancellationToken);
            return ReadArray(json.RootElement, "Tcp").SelectMany(profile =>
            {
                var name = ReadString(profile, "SettingName") ?? "Perfil sem nome";
                return profile.EnumerateObject().Where(p => p.Name != "SettingName").Select(p => new HardwareFact(
                    name + " · " + p.Name, p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.ToString(), "Get-NetTCPSetting",
                    p.Value.ValueKind == JsonValueKind.Null ? Availability.Unsupported : Availability.Available,
                    "Perfil TCP disponível; não comprova qual perfil uma conexão usa. UDP de jogos não usa esses ajustes TCP."));
            }).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or TimeoutException or Win32Exception or UnauthorizedAccessException)
        {
            return [new("Perfis TCP", null, "Get-NetTCPSetting", ErrorStatus(ex), PublicError(ex))];
        }
    }

    public async Task<NetworkProperty> GetPropertyAsync(Guid interfaceGuid, string registryKeyword,
        CancellationToken cancellationToken = default)
    {
        ValidateKeyword(registryKeyword);
        var detail = await InspectAdapterAsync(interfaceGuid, cancellationToken);
        if (detail.Status != Availability.Available)
            throw new InvalidOperationException(string.Join("; ", detail.Limitations));
        return detail.AdvancedProperties.SingleOrDefault(p => string.Equals(p.RegistryKeyword, registryKeyword, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Propriedade técnica não encontrada no driver atual.");
    }

    /// <summary>This version is consultation-only, including direct API callers.</summary>
    public Task SetPropertyAsync(Guid interfaceGuid, string registryKeyword, IReadOnlyList<string> registryValues,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Esta versão apenas consulta a rede. Nenhuma configuração pode ser alterada pelo PC Analyzer.");

    public Task SetPropertyExpectedAsync(Guid interfaceGuid, string registryKeyword,
        IReadOnlyList<string> registryValues, IReadOnlyList<string> expectedCurrentValues,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Esta versão apenas consulta a rede. Nenhuma configuração pode ser alterada pelo PC Analyzer.");

    public static void ValidateKeyword(string registryKeyword)
    {
        if (string.IsNullOrWhiteSpace(registryKeyword) || registryKeyword.Length > 128 ||
            !Regex.IsMatch(registryKeyword, @"\A[A-Za-z0-9_*.-]+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Identificador técnico RegistryKeyword inválido.", nameof(registryKeyword));
    }
    private static void RequireGuid(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("O adaptador deve possuir um InterfaceGuid válido.");
    }
    private static AdapterDetail Unavailable(Guid id, Availability status, string error) =>
        new(id, "", "", null, null, null, null, [], [], [], status, [error]);

    private static IReadOnlyList<JsonElement> ReadArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
    }
    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
    private static IReadOnlyList<string> ReadStrings(JsonElement parent, string name) =>
        ReadArray(parent, name).Where(x => x.ValueKind != JsonValueKind.Null).Select(x => x.ToString()).ToArray();
    private static IReadOnlyList<HardwareFact> ReadFacts(JsonElement parent, string name, string source)
    {
        if (!parent.TryGetProperty(name, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            var state = parent.TryGetProperty("Statuses", out var states) && states.ValueKind == JsonValueKind.Object &&
                Enum.TryParse<Availability>(ReadString(states, name), out var status) ? status : Availability.Unsupported;
            return [new(name, null, source, state, "Esta fonte não retornou dados; consulte as limitações da conexão.")];
        }
        return obj.EnumerateObject().Select(p => new HardwareFact(p.Name,
            p.Value.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(p.Value.ToString()) ? null : p.Value.ToString(), source,
            p.Value.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(p.Value.ToString()) ? Availability.Unsupported : Availability.Available)).ToArray();
    }

    private static async Task<JsonDocument> RunNetAdapterAsync(object request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NetAdapter exige Windows.");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) throw new IOException("Windows PowerShell não encontrado na pasta de sistema.");
        var options = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(NetAdapterScript)) }) options.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = options };
        if (!process.Start()) throw new InvalidOperationException("Falha ao iniciar Windows PowerShell.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var outputTask = ReadBoundedAsync(process.StandardOutput, 1024 * 1024, deadline.Token);
        var errorsTask = ReadBoundedAsync(process.StandardError, 64 * 1024, deadline.Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var output = await outputTask;
            var errors = await errorsTask;
            if (!string.IsNullOrWhiteSpace(errors)) PowerShellErrorText.Log(errors);
            if (process.ExitCode != 0)
            {
                var normalized = PowerShellErrorText.Read(errors);
                var denied = normalized.Contains("Access", StringComparison.OrdinalIgnoreCase) || normalized.Contains("Acesso", StringComparison.OrdinalIgnoreCase);
                throw new NetworkReadException(denied ? Availability.PermissionRequired : Availability.ReadError,
                    denied ? "O Windows negou acesso à consulta de rede. Nenhuma elevação foi iniciada."
                    : "Não foi possível consultar o driver desta conexão. Atualize a lista; detalhes estão no log de rede.");
            }
            return JsonDocument.Parse(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("NetAdapter excedeu o limite de 30 segundos.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
            // Observe pipe-reader failures even when process wait or input failed.
            try { await Task.WhenAll(outputTask, errorsTask); } catch (Exception) { }
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters, CancellationToken token)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + count > maxCharacters) throw new IOException("Saída NetAdapter excedeu o limite de segurança.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    // Only this constant is executed. All caller data is JSON on stdin and becomes typed cmdlet parameters.
    // Do not add Invoke-Expression, user scripts, or shell command strings here.
    internal const string NetAdapterScript = """
        $ErrorActionPreference = 'Stop'
        $WarningPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        try {
          $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
          if ($request.Action -ceq 'Tcp') {
            $settings = @(Get-NetTCPSetting | Select-Object SettingName, AutoTuningLevelLocal, AutoTuningLevelGroupPolicy,
              CongestionProvider, EcnCapability, Timestamps, ScalingHeuristics, MinRtoMs, InitialRtoMs, DelayedAckTimeoutMs, DelayedAckFrequency)
            @{ Tcp = $settings } | ConvertTo-Json -Depth 5 -Compress
            exit 0
          }
          $id = [guid]::Parse([string]$request.InterfaceGuid)
          if ($id -eq [guid]::Empty) { throw 'Empty adapter GUID.' }
          function Guid-Equals($value, [guid]$expected) {
            $parsed = [guid]::Empty
            return [guid]::TryParse([string]$value, [ref]$parsed) -and $parsed -eq $expected
          }
          $all = @(Get-NetAdapter -IncludeHidden)
          $matches = @($all | Where-Object { Guid-Equals $_.InterfaceGuid $id })
          $method = 'InterfaceGuid normalizado'
          if ($matches.Count -gt 1) { throw 'Mais de uma interface informa a mesma identidade. Consulta interrompida para evitar associar outro dispositivo.' }
          if ($matches.Count -eq 0) {
            # Some Intel/provider versions omit or format InterfaceGuid differently. Map through the
            # legacy class only with the exact requested GUID, index AND PnP identity; names are not IDs.
            $legacy = @(Get-CimInstance -ClassName Win32_NetworkAdapter | Where-Object { Guid-Equals $_.GUID $id })
            if ($legacy.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$legacy[0].PNPDeviceID) -or $null -eq $legacy[0].InterfaceIndex) {
              throw 'A conexão não pôde ser associada de forma única ao driver. Atualize a lista de conexões e tente novamente.'
            }
            $matches = @($all | Where-Object {
              $candidate = [guid]::Empty
              $hasGuid = [guid]::TryParse([string]$_.InterfaceGuid, [ref]$candidate) -and $candidate -ne [guid]::Empty
              (-not $hasGuid -or $candidate -eq $id) -and
                $null -ne $_.ifIndex -and [uint32]$_.ifIndex -eq [uint32]$legacy[0].InterfaceIndex -and
                [string]$_.PnPDeviceID -ieq [string]$legacy[0].PNPDeviceID
            })
            $method = 'GUID Win32 + índice + PnP verificados'
          }
          if ($matches.Count -ne 1) { throw 'O driver não forneceu uma associação única para esta conexão. Nenhum outro adaptador foi usado.' }
          $adapter = $matches[0]
          if ([string]::IsNullOrWhiteSpace([string]$adapter.Name) -or
              @($all | Where-Object { [string]$_.Name -ieq [string]$adapter.Name }).Count -ne 1) {
            throw 'O nome retornado pelo driver não é único. Consulta interrompida para não misturar conexões.'
          }
          $literalName = [System.Management.Automation.WildcardPattern]::Escape([string]$adapter.Name)
          if ($request.Action -ceq 'Inspect') {
            $limits = New-Object 'System.Collections.Generic.List[string]'
            $statuses = @{}
            function Read-Failed([string]$section, $failure) {
              $detail = [string]$failure.Exception.Message
              $state = if ($failure.CategoryInfo.Category -eq 'PermissionDenied' -or $detail -match 'access|acesso') { 'PermissionRequired' }
                elseif ($failure.CategoryInfo.Category -eq 'NotImplemented' -or $detail -match 'not supported|não suportad') { 'Unsupported' }
                else { 'ReadError' }
              $statuses[$section] = $state
              $limits.Add($section + ': leitura não disponível; o motivo técnico está no log de rede.')
              [Console]::Error.WriteLine($section + ': ' + [string]$failure)
            }
            $properties = @()
            try {
              $properties = @(Get-NetAdapterAdvancedProperty -Name $literalName -IncludeHidden -AllProperties | ForEach-Object {
                @{ RegistryKeyword = [string]$_.RegistryKeyword; DisplayName = [string]$_.DisplayName;
                   DisplayValue = [string]$_.DisplayValue; RegistryValue = @($_.RegistryValue | ForEach-Object { [string]$_ });
                   ValidRegistryValues = @($_.ValidRegistryValues | ForEach-Object { [string]$_ });
                   ValidDisplayValues = @($_.ValidDisplayValues | ForEach-Object { [string]$_ }) }
              })
            } catch { Read-Failed 'Properties' $_ }
            $rss = $null
            try {
              $r = Get-NetAdapterRss -Name $literalName -IncludeHidden
              $rss = @{ Enabled = $r.Enabled; Profile = [string]$r.Profile;
                NumberOfReceiveQueues = $r.NumberOfReceiveQueues; BaseProcessorGroup = $r.BaseProcessorGroup; BaseProcessorNumber = $r.BaseProcessorNumber;
                MaxProcessorGroup = $r.MaxProcessorGroup; MaxProcessorNumber = $r.MaxProcessorNumber; MaxProcessors = $r.MaxProcessors;
                RssProcessorArray = @($r.RssProcessorArray | ForEach-Object { [string]$_ }) -join "; " }
            } catch { Read-Failed 'Rss' $_ }
            $stats = $null
            try {
              $s = Get-NetAdapterStatistics -Name $literalName -IncludeHidden
              $stats = @{ ReceivedBytes = $s.ReceivedBytes; SentBytes = $s.SentBytes;
                ReceivedPacketErrors = $s.ReceivedPacketErrors; OutboundPacketErrors = $s.OutboundPacketErrors;
                ReceivedDiscardedPackets = $s.ReceivedDiscardedPackets; OutboundDiscardedPackets = $s.OutboundDiscardedPackets }
            } catch { Read-Failed 'Statistics' $_ }
            $power = $null
            try {
              $p = Get-NetAdapterPowerManagement -Name $literalName -IncludeHidden
              $power = @{ AllowComputerToTurnOffDevice = [string]$p.AllowComputerToTurnOffDevice;
                DeviceSleepOnDisconnect = [string]$p.DeviceSleepOnDisconnect;
                SelectiveSuspend = [string]$p.SelectiveSuspend; WakeOnMagicPacket = [string]$p.WakeOnMagicPacket;
                WakeOnPattern = [string]$p.WakeOnPattern; ArpOffload = [string]$p.ArpOffload; NSOffload = [string]$p.NSOffload }
            } catch { Read-Failed 'Power' $_ }
            @{ Name = $adapter.Name; Description = $adapter.InterfaceDescription; DriverVersion = [string]$adapter.DriverVersion;
               DriverInformation = [string]$adapter.DriverInformation; DriverDate = [string]$adapter.DriverDate;
               Identity = @{ Mapping = $method; InterfaceIndex = $adapter.ifIndex; PnPDeviceID = [string]$adapter.PnPDeviceID;
                 HardwareInterface = $adapter.HardwareInterface; InterfaceGuid = $id.ToString() };
               LinkSpeed = [string]$adapter.LinkSpeed; Properties = @($properties); Rss = $rss; Statistics = $stats; Power = $power;
               Statuses = $statuses; Limitations = @($limits.ToArray()) } | ConvertTo-Json -Depth 8 -Compress
          } else { throw 'Unsupported action.' }
        } catch {
          [Console]::Error.WriteLine($_.Exception.Message)
          exit 1
        }
        """;

    private sealed class NetworkReadException(Availability status, string message) : InvalidOperationException(message)
    {
        public Availability Status { get; } = status;
    }
}
