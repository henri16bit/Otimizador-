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
        if (!OperatingSystem.IsWindows()) limitations.Add("NetAdapter e alterações de rede exigem Windows; IDs não GUID foram excluídos.");
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
            var root = json.RootElement;
            var properties = ReadArray(root, "Properties").Select(p =>
            {
                var keyword = ReadString(p, "RegistryKeyword") ?? "";
                var guidance = PropertyGuidance.For(keyword);
                return new NetworkProperty(keyword, ReadString(p, "DisplayName") ?? keyword,
                    ReadStrings(p, "RegistryValue"), ReadString(p, "DisplayValue"),
                    ReadStrings(p, "ValidRegistryValues"), ReadStrings(p, "ValidDisplayValues"),
                    guidance.Function, guidance.Consequences, guidance.Recommendation, guidance.ExpectedImpact);
            }).Where(p => p.RegistryKeyword.Length > 0).ToArray();
            var limitations = ReadStrings(root, "Limitations");
            return new(interfaceGuid, ReadString(root, "Name") ?? "", ReadString(root, "Description") ?? "",
                ReadString(root, "DriverVersion"), ReadString(root, "DriverInformation"), ReadString(root, "DriverDate"),
                ReadString(root, "LinkSpeed"), properties, ReadFacts(root, "Rss", "Get-NetAdapterRss"),
                ReadFacts(root, "Statistics", "Get-NetAdapterStatistics"), Availability.Available, limitations,
                PowerManagement: ReadFacts(root, "Power", "Get-NetAdapterPowerManagement"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or TimeoutException or Win32Exception or UnauthorizedAccessException)
        {
            var status = ex is Win32Exception { NativeErrorCode: 2 } ||
                ex is IOException && ex.Message.Contains("Windows PowerShell não encontrado", StringComparison.Ordinal)
                ? Availability.ToolMissing
                : ex is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } || ex.Message.Contains("Access", StringComparison.OrdinalIgnoreCase) ||
                  ex.Message.Contains("Acesso", StringComparison.OrdinalIgnoreCase)
                    ? Availability.PermissionRequired : Availability.ReadError;
            return Unavailable(interfaceGuid, status, ex.Message);
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

    /// <summary>Called by the explicit elevated worker only. This method never elevates or restarts an adapter.</summary>
    public async Task SetPropertyAsync(Guid interfaceGuid, string registryKeyword, IReadOnlyList<string> registryValues,
        CancellationToken cancellationToken = default)
    {
        var current = await GetPropertyAsync(interfaceGuid, registryKeyword, cancellationToken);
        await SetPropertyExpectedAsync(interfaceGuid, registryKeyword, registryValues, current.RegistryValues, cancellationToken);
    }

    public async Task SetPropertyExpectedAsync(Guid interfaceGuid, string registryKeyword,
        IReadOnlyList<string> registryValues, IReadOnlyList<string> expectedCurrentValues,
        CancellationToken cancellationToken = default)
    {
        RequireGuid(interfaceGuid);
        ValidateKeyword(registryKeyword);
        ArgumentNullException.ThrowIfNull(registryValues);
        if (registryValues.Count != 1 || registryValues[0] is null || registryValues[0].Length > 128)
            throw new ArgumentException("Somente propriedades escalares com um valor suportado são editáveis.", nameof(registryValues));
        ArgumentNullException.ThrowIfNull(expectedCurrentValues);
        if (expectedCurrentValues.Count != 1 || expectedCurrentValues[0] is null || expectedCurrentValues[0].Length > 128)
            throw new ArgumentException("O valor original deve conter uma propriedade escalar.", nameof(expectedCurrentValues));
        // The fixed worker script rediscovers and validates the adapter, keyword and values in the privileged process.
        using var result = await RunNetAdapterAsync(new
        {
            Action = "Write", InterfaceGuid = interfaceGuid, RegistryKeyword = registryKeyword,
            RegistryValues = registryValues.ToArray(), ExpectedRegistryValues = expectedCurrentValues.ToArray()
        }, cancellationToken);
        if (!result.RootElement.TryGetProperty("Written", out var written) || written.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("NetAdapter não confirmou execução da alteração.");
    }

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
                return [new(name, null, source, Availability.Unsupported, "Dados não disponíveis — verifique permissões ou suporte do dispositivo.")];
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
            if (process.ExitCode != 0) throw new InvalidOperationException("NetAdapter: " + errors.Trim());
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
    private const string NetAdapterScript = """
        $ErrorActionPreference = 'Stop'
        $WarningPreference = 'SilentlyContinue'
        [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        try {
          $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
          $id = [guid]::Parse([string]$request.InterfaceGuid)
          if ($id -eq [guid]::Empty) { throw 'Empty adapter GUID.' }
          $adapter = @(Get-NetAdapter -IncludeHidden | Where-Object { $_.InterfaceGuid -eq $id })
          if ($adapter.Count -ne 1) { throw 'Adapter GUID not found or ambiguous.' }
          $adapter = $adapter[0]
          $literalName = [System.Management.Automation.WildcardPattern]::Escape([string]$adapter.Name)
          if ($request.Action -ceq 'Write') {
            $keyword = [string]$request.RegistryKeyword
            if ($keyword.Length -gt 128 -or $keyword -cnotmatch '^[A-Za-z0-9_*.-]+$') { throw 'Invalid RegistryKeyword.' }
            $property = @(Get-NetAdapterAdvancedProperty -Name $literalName -IncludeHidden -AllProperties |
              Where-Object { $_.RegistryKeyword -ceq $keyword })
            if ($property.Count -ne 1) { throw 'Supported property not found or ambiguous.' }
            $values = @($request.RegistryValues | ForEach-Object { [string]$_ })
            if ($values.Count -ne 1 -or @($property[0].RegistryValue).Count -ne 1) { throw 'Only scalar driver properties can be written.' }
            $valid = @($property[0].ValidRegistryValues | ForEach-Object { [string]$_ })
            if ($valid.Count -eq 0 -or $valid -cnotcontains $values[0]) { throw 'Value not advertised as valid by the current driver.' }
            $expected = @($request.ExpectedRegistryValues | ForEach-Object { [string]$_ })
            $current = @($property[0].RegistryValue | ForEach-Object { [string]$_ })
            if ($expected.Count -ne 1 -or $current.Count -ne 1 -or $current[0] -cne $expected[0]) {
              throw 'Current driver property differs from the journaled original; no write was executed.'
            }
            # InputObject selects the exact property instance; RegistryKeyword patterns such as '*RSS' are never used as wildcards.
            Set-NetAdapterAdvancedProperty -InputObject $property[0] -RegistryValue $values -NoRestart -Confirm:$false
            @{ Written = $true; RestartSuppressed = $true } | ConvertTo-Json -Compress
          } elseif ($request.Action -ceq 'Inspect') {
            $limits = New-Object 'System.Collections.Generic.List[string]'
            $properties = @()
            try {
              $properties = @(Get-NetAdapterAdvancedProperty -Name $literalName -IncludeHidden -AllProperties | ForEach-Object {
                @{ RegistryKeyword = [string]$_.RegistryKeyword; DisplayName = [string]$_.DisplayName;
                   DisplayValue = [string]$_.DisplayValue; RegistryValue = @($_.RegistryValue | ForEach-Object { [string]$_ });
                   ValidRegistryValues = @($_.ValidRegistryValues | ForEach-Object { [string]$_ });
                   ValidDisplayValues = @($_.ValidDisplayValues | ForEach-Object { [string]$_ }) }
              })
            } catch { $limits.Add('Advanced properties: ' + $_.Exception.Message) }
            $rss = $null
            try {
              $r = Get-NetAdapterRss -Name $literalName -IncludeHidden
              $rss = @{ Enabled = $r.Enabled; Profile = [string]$r.Profile;
                NumberOfReceiveQueues = $r.NumberOfReceiveQueues; BaseProcessorNumber = $r.BaseProcessorNumber;
                MaxProcessorNumber = $r.MaxProcessorNumber; MaxProcessors = $r.MaxProcessors }
            } catch { $limits.Add('RSS: ' + $_.Exception.Message) }
            $stats = $null
            try {
              $s = Get-NetAdapterStatistics -Name $literalName -IncludeHidden
              $stats = @{ ReceivedBytes = $s.ReceivedBytes; SentBytes = $s.SentBytes;
                ReceivedPacketErrors = $s.ReceivedPacketErrors; OutboundPacketErrors = $s.OutboundPacketErrors;
                ReceivedDiscardedPackets = $s.ReceivedDiscardedPackets; OutboundDiscardedPackets = $s.OutboundDiscardedPackets }
            } catch { $limits.Add('Statistics: ' + $_.Exception.Message) }
            $power = $null
            try {
              $p = Get-NetAdapterPowerManagement -Name $literalName -IncludeHidden
              $power = @{ AllowComputerToTurnOffDevice = [string]$p.AllowComputerToTurnOffDevice;
                DeviceSleepOnDisconnect = [string]$p.DeviceSleepOnDisconnect;
                SelectiveSuspend = [string]$p.SelectiveSuspend; WakeOnMagicPacket = [string]$p.WakeOnMagicPacket;
                WakeOnPattern = [string]$p.WakeOnPattern; ArpOffload = [string]$p.ArpOffload; NSOffload = [string]$p.NSOffload }
            } catch { $limits.Add('Power management: ' + $_.Exception.Message) }
            @{ Name = $adapter.Name; Description = $adapter.InterfaceDescription; DriverVersion = [string]$adapter.DriverVersion;
               DriverInformation = [string]$adapter.DriverInformation; DriverDate = [string]$adapter.DriverDate;
               LinkSpeed = [string]$adapter.LinkSpeed; Properties = @($properties); Rss = $rss; Statistics = $stats; Power = $power;
               Limitations = @($limits.ToArray()) } | ConvertTo-Json -Depth 8 -Compress
          } else { throw 'Unsupported action.' }
        } catch {
          [Console]::Error.WriteLine($_.Exception.Message)
          exit 1
        }
        """;
}
