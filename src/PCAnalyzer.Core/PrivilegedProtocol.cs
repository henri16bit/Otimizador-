namespace PCAnalyzer.Core;

public enum PrivilegedAction { CaptureDpc, SetPowerPlan, SetAdapterProperty }
public sealed record PrivilegedRequest(PrivilegedAction Action, DateTimeOffset CreatedAt,
    Guid TargetGuid = default, string? Keyword = null, string? Value = null,
    string? ExpectedOriginal = null, int DurationSeconds = 30);
public sealed record PrivilegedResponse(bool Success, string Detail, string? PayloadJson = null);

public static class PrivilegedProtocol
{
    public static string RequestDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCAnalyzer", "requests");
    public static string RequestPath(Guid id) => Path.Combine(RequestDirectory, id.ToString("N") + ".request.json");
    public static string ResponsePath(Guid id) => Path.Combine(RequestDirectory, id.ToString("N") + ".response.json");
    public static string CancellationPath(Guid id) => Path.Combine(RequestDirectory, id.ToString("N") + ".cancel");
}
