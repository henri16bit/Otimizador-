using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

internal sealed record NvDrsTarget(string Executable, IReadOnlyList<int> ProcessIds, bool FullPath);
internal sealed record NvDrsProcessSnapshot(IReadOnlyList<NvDrsTarget> Targets, int ProcessCount,
    int InaccessiblePaths, bool Truncated);

/// <summary>Reads a driver-owned DRS snapshot. No setting/save, process mutation or elevation API exists here.</summary>
public sealed class NvidiaDriverAuditProvider : IPerformanceAuditProvider
{
    private const string Source = "NVAPI DRS pública do driver instalado";
    private const string GameExecutable = "VALORANT-Win64-Shipping.exe";
    private const string EffectiveLimit = "A leitura confirma a preferência reconhecida pelo DRS, não seu efeito durante a renderização nem benefício de desempenho.";
    private readonly Func<INvapiDrsReader> _open;
    private readonly Func<NvDrsProcessSnapshot> _processes;

    public NvidiaDriverAuditProvider() : this(() => new NvapiDrsReader(), ReadProcesses) { }
    internal NvidiaDriverAuditProvider(Func<INvapiDrsReader> open, Func<NvDrsProcessSnapshot> processes)
    { _open = open; _processes = processes; }

    public IReadOnlyList<AuditRule> Rules { get; } = NvidiaAuditSettings.All.Select(NvidiaAuditSettings.Rule).Concat(
    [
        Rule("nvidia.profiles", "Perfis base e global", "DRS GetBaseProfile/GetCurrentGlobalProfile/GetProfileInfo", AuditSupport.Documented,
            "Um perfil encontrado não demonstra que uma opção afeta o jogo."),
        Rule("nvidia.applications", "Perfis dos aplicativos", "DRS FindApplicationByName usando caminhos dos processos observados", AuditSupport.Conditional,
            "A consulta pelo nome é menos específica que pelo caminho. Processo presente não comprova uso da GPU nem API gráfica; restrições adicionais do perfil são preservadas."),
        Rule("nvidia.low-latency", "Low Latency Mode", "Verificação da cobertura da API pública", AuditSupport.NotVerifiable,
            "O catálogo público consultado não oferece um ID documentado que permita confirmar Low Latency Mode. Maximum Pre-Rendered Frames é outra propriedade."),
        Rule("nvidia.reflex", "NVIDIA Reflex no jogo", "Verificação da cobertura da API pública", AuditSupport.NotVerifiable,
            "O estado interno de Reflex não é exposto pela consulta DRS implementada; não é inferido de arquivos privados, nome do jogo ou telemetria.")
    ]).ToArray();

    public Task<IReadOnlyList<AuditObservation>> ReadAsync(CancellationToken ct) => Task.Run(() => Read(ct), ct);

    private IReadOnlyList<AuditObservation> Read(CancellationToken ct)
    {
        var rows = new List<AuditObservation>();
        foreach (var rule in Rules.Where(x => x.Support == AuditSupport.NotVerifiable))
            rows.Add(new(rule.Id, rule.Id, rule.Name, "Driver / jogo", null, Availability.InsufficientData,
                Source, new Dictionary<string, string> { ["recognition"] = "Não verificada pela API implementada" }, rule.Limitations));
        INvapiDrsReader reader;
        try { ct.ThrowIfCancellationRequested(); reader = _open(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            foreach (var rule in Rules.Where(x => x.Support != AuditSupport.NotVerifiable))
                rows.Add(Failure(rule.Id, rule.Id, rule.Name, "Driver NVIDIA", error));
            return rows;
        }
        using (reader)
        {
            var global = ReadProfile(reader, reader.GetGlobalProfile, "global", "Perfil global", rows, null, ct);
            ReadProfile(reader, reader.GetBaseProfile, "base", "Perfil base", rows, null, ct);
            NvDrsProcessSnapshot snapshot;
            try { snapshot = _processes(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                rows.Add(Failure("nvidia.applications", "processes", "Processos para consulta de perfis", "Aplicativos", error));
                snapshot = new([], 0, 0, false);
            }
            var targets = snapshot.Targets.Take(256).ToList();
            if (!targets.Any(x => FileName(x.Executable).Equals(GameExecutable, StringComparison.OrdinalIgnoreCase)))
                targets.Insert(0, new(GameExecutable, [], false));
            var absent = 0;
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Executable.ToUpperInvariant())))[..16];
                var scope = "Aplicativo: " + FileName(target.Executable);
                try
                {
                    var app = reader.FindApplication(target.Executable);
                    var facts = TargetFacts(target);
                    facts["presence"] = "present";
                    facts["configured"] = "false";
                    facts["profile"] = app.Profile.Name;
                    facts["applicationName"] = app.Executable;
                    facts["predefinedAssociation"] = app.Predefined ? "true" : "false";
                    facts["fileInFolder"] = app.FileInFolder;
                    facts["applicationFlags"] = $"0x{app.Flags:X8}";
                    facts["commandLine"] = app.CommandLine;
                    facts["structureVersion"] = app.StructureVersion.ToString(CultureInfo.InvariantCulture);
                    facts["effective"] = target.FullPath
                        ? "A NVAPI associou o caminho observado a este perfil. Não foi medido o efeito das opções no jogo."
                        : "A NVAPI encontrou uma associação pelo nome; o caminho completo não foi confirmado.";
                    var limitation = target.ProcessIds.Count == 0
                        ? "Perfil consultado como candidato; nenhum processo com este executável foi observado. Isso não comprova instalação ou execução do jogo."
                        : "Associação capturada no instante da consulta; o processo pode encerrar ou trocar de estado.";
                    if (!target.FullPath) limitation += " Caminho indisponível: restrições por pasta/linha de comando podem mudar a seleção efetiva.";
                    if (!string.IsNullOrEmpty(app.FileInFolder) || app.Flags != 0 || !string.IsNullOrEmpty(app.CommandLine))
                        limitation += " O perfil possui critérios adicionais registrados nos detalhes; não foram validados independentemente.";
                    rows.Add(new("nvidia.applications", key, "Perfil de " + FileName(target.Executable), scope,
                        app.Profile.Name, Availability.Available, Source, facts, limitation));
                    ReadSettings(reader, app.Profile, key, scope, rows, global, ct, facts);
                }
                catch (NvDrsException error) when (error.Status == -166)
                {
                    absent++;
                    // Keep the requested game visible; routine processes without a DRS association are summarized.
                    if (FileName(target.Executable).Equals(GameExecutable, StringComparison.OrdinalIgnoreCase))
                    {
                        var facts = TargetFacts(target);
                        facts["presence"] = "absent";
                        rows.Add(new("nvidia.applications", key, "Perfil de " + FileName(target.Executable), scope, null,
                            Availability.Available, Source, facts,
                            "DRS não encontrou associação para esta consulta. Isso não significa que o jogo esteja ausente ou que as preferências globais não se apliquem."));
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { rows.Add(Failure("nvidia.applications", key, "Consulta de " + FileName(target.Executable), scope, error)); }
            }
            rows.Add(new("nvidia.applications", "coverage", "Cobertura da consulta de perfis", "Aplicativos", $"{targets.Count} consultas",
                Availability.Available, Source, new Dictionary<string, string>
                {
                    ["configured"] = "false", ["processCount"] = snapshot.ProcessCount.ToString(CultureInfo.InvariantCulture),
                    ["inaccessiblePaths"] = snapshot.InaccessiblePaths.ToString(CultureInfo.InvariantCulture),
                    ["withoutAssociation"] = absent.ToString(CultureInfo.InvariantCulture),
                    ["effective"] = "Foram consultados executáveis observados e o candidato VALORANT; não houve inspeção da memória dos jogos."
                }, snapshot.Truncated || snapshot.Targets.Count > 256
                    ? "Consulta limitada aos primeiros 256 executáveis observados, priorizando VALORANT; demais processos não foram avaliados."
                    : "Perfis de aplicativos não observados, exceto o candidato VALORANT, não são enumerados."));
        }
        return rows;
    }

    private static Dictionary<uint, NvDrsSetting> ReadProfile(INvapiDrsReader reader, Func<NvDrsProfile> getProfile,
        string key, string scope, List<AuditObservation> rows, Dictionary<uint, NvDrsSetting>? global, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var profile = getProfile();
            rows.Add(new("nvidia.profiles", key, scope, scope, profile.Name, Availability.Available, Source,
                new Dictionary<string, string> { ["presence"] = "present", ["configured"] = "false",
                    ["predefinedProfile"] = profile.Predefined ? "true" : "false",
                    ["effective"] = "Perfil retornado pelo DRS: " + profile.Name }, EffectiveLimit));
            return ReadSettings(reader, profile, key, scope, rows, global, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { rows.Add(Failure("nvidia.profiles", key, scope, scope, error)); return []; }
    }

    private static Dictionary<uint, NvDrsSetting> ReadSettings(INvapiDrsReader reader, NvDrsProfile profile, string key,
        string scope, List<AuditObservation> rows, Dictionary<uint, NvDrsSetting>? global, CancellationToken ct,
        Dictionary<string, string>? association = null)
    {
        var values = new Dictionary<uint, NvDrsSetting>();
        foreach (var definition in NvidiaAuditSettings.All)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var setting = reader.ReadSetting(profile.Handle, definition.Id);
                if (setting.Type != 0) throw new InvalidDataException("A API retornou um tipo diferente de DWORD para este ID público; o conteúdo não foi interpretado.");
                values.Add(definition.Id, setting);
                var origin = setting.Location switch { 0 => "Perfil consultado", 1 => "Herdado do perfil global",
                    2 => "Herdado do perfil base", 3 => "Padrão do driver", _ => "Origem não reconhecida" };
                var facts = new Dictionary<string, string>
                {
                    ["presence"] = "present", ["configured"] = setting.CurrentPredefined ? "false" : "true",
                    ["profile"] = profile.Name, ["settingId"] = $"0x{setting.Id:X8}", ["settingType"] = "DWORD",
                    ["decimal"] = setting.CurrentValue.ToString(CultureInfo.InvariantCulture),
                    ["hexadecimal"] = $"0x{setting.CurrentValue:X8}", ["origin"] = origin,
                    ["location"] = setting.Location.ToString(CultureInfo.InvariantCulture),
                    ["currentPredefined"] = setting.CurrentPredefined ? "true" : "false",
                    ["explicitInProfile"] = setting.Location == 0 && !setting.CurrentPredefined ? "true" : "false",
                    ["recognition"] = "GetSetting retornou este ID e valor com sucesso; aplicabilidade na renderização não medida."
                };
                if (setting.PredefinedValid) facts["predefinedValue"] = $"{definition.Format(setting.PredefinedValue)} (0x{setting.PredefinedValue:X8})";
                if (association is not null)
                {
                    facts["runningProcessIds"] = association["runningProcessIds"];
                    facts["lookup"] = association["lookup"];
                    if (global?.TryGetValue(definition.Id, out var globalSetting) == true)
                    {
                        facts["globalValue"] = $"{definition.Format(globalSetting.CurrentValue)} (0x{globalSetting.CurrentValue:X8})";
                        facts["differsFromGlobal"] = setting.CurrentValue != globalSetting.CurrentValue ? "true" : "false";
                        if (setting.CurrentValue != globalSetting.CurrentValue)
                            facts["comparison"] = "Este perfil possui um valor diferente do global. A associação DRS pode prevalecer no aplicativo; falta uma captura comparável para demonstrar efeito.";
                    }
                }
                // No 'effective' fact: DRS preference resolution is not observed rendering behavior.
                rows.Add(new(definition.RuleId, key + ":" + definition.Key, definition.Name, scope,
                    $"{definition.Format(setting.CurrentValue)} (0x{setting.CurrentValue:X8}; {setting.CurrentValue})",
                    setting.Location <= 3 ? Availability.Available : Availability.InsufficientData, Source, facts,
                    origin + ". " + (setting.CurrentPredefined ? "Valor predefinido do driver. " : "Valor de usuário. ") + EffectiveLimit + " " + definition.Applicability));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { rows.Add(Failure(definition.RuleId, key + ":" + definition.Key, definition.Name, scope, error)); }
        }
        return values;
    }

    private static Dictionary<string, string> TargetFacts(NvDrsTarget target) => new()
    {
        ["lookup"] = target.FullPath ? "Caminho completo observado" : "Somente nome do executável",
        ["executable"] = target.Executable,
        ["runningProcessIds"] = string.Join(", ", target.ProcessIds),
        ["observedProcess"] = target.ProcessIds.Count > 0 ? "true" : "false"
    };

    private static AuditObservation Failure(string ruleId, string instance, string name, string scope, Exception error)
    {
        var status = error switch
        {
            PlatformNotSupportedException or EntryPointNotFoundException => Availability.Unsupported,
            DllNotFoundException => Availability.ToolMissing,
            UnauthorizedAccessException => Availability.PermissionRequired,
            NvDrsException { Status: -137 } => Availability.PermissionRequired,
            NvDrsException { Status: -2 or -3 or -6 or -9 or -104 or -160 } => Availability.Unsupported,
            NvDrsException { Status: -182 } => Availability.InsufficientData,
            _ => Availability.ReadError
        };
        var reason = error is NvDrsException native ? native.Status switch
        {
            -160 => "O driver não retornou este ID nesta consulta; ausência e falta de suporte não foram diferenciadas. Nenhum valor padrão foi inventado.",
            -182 => "O nome corresponde a mais de uma associação DRS. O caminho completo é necessário; nenhum perfil foi escolhido arbitrariamente.",
            -137 => "O driver negou a leitura por permissão. Nenhuma elevação foi iniciada.",
            _ => $"A consulta DRS falhou (NVAPI {native.Status}); isto não demonstra um problema de desempenho."
        } : error.Message;
        var facts = new Dictionary<string, string>();
        if (error is NvDrsException nv) facts["nvapiStatus"] = nv.Status.ToString(CultureInfo.InvariantCulture);
        return new(ruleId, instance, name, scope, null, status, Source, facts, reason);
    }

    private static AuditRule Rule(string id, string name, string method, AuditSupport support, string limitation) =>
        new(id, "NVIDIA", name, method, "Driver NVIDIA com NVAPI DRS acessível.", "Windows x64; suporte varia com driver.",
            "Resposta da API, origem e contexto de processo; captura real para avaliar impacto.",
            "Identificar preferências e diferenças; nenhum ganho é presumido.", support, limitation,
            "Conferir o perfil no Painel NVIDIA e comparar capturas equivalentes do mesmo aplicativo/API.",
            [NvidiaAuditSettings.HeaderSource, NvidiaAuditSettings.ApiSource]);

    private static string FileName(string value) => value.Replace('\\', '/').Split('/')[^1];

    private static NvDrsProcessSnapshot ReadProcesses()
    {
        var values = new List<(string Executable, int Id, bool FullPath)>();
        var inaccessible = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = process.ProcessName; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                if (string.IsNullOrWhiteSpace(name) || process.Id == 0) continue;
                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                if (string.IsNullOrWhiteSpace(path)) inaccessible++;
                values.Add((path ?? (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe"), process.Id, !string.IsNullOrWhiteSpace(path)));
            }
        }
        var grouped = values.GroupBy(x => x.Executable, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => FileName(g.Key).Contains("VALORANT", StringComparison.OrdinalIgnoreCase)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(grouped.Take(256).Select(g => new NvDrsTarget(g.Key, g.Select(x => x.Id).ToArray(), g.All(x => x.FullPath))).ToArray(),
            values.Count, inaccessible, grouped.Length > 256);
    }
}
