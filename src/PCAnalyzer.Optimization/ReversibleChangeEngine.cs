using PCAnalyzer.Core;

namespace PCAnalyzer.Optimization;

public sealed record ChangePreview(string Kind, string Target, string Before, string After, ChangeRisk Risk,
    string RecoveryInstructions)
{
    public bool HasChange => !string.Equals(Before, After, StringComparison.Ordinal);
}

/// <summary>
/// Compatibility surface for historical change records and read-only comparisons.
/// Applying or restoring configuration is not supported by the audit edition.
/// </summary>
public sealed class ReversibleChangeEngine
{
    public ReversibleChangeEngine(IHistoryStore history) => ArgumentNullException.ThrowIfNull(history);

    public async Task<ChangePreview> PreviewAsync(IReversibleOperation operation, string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(value);
        var before = await operation.ReadAsync(cancellationToken);
        await operation.ValidateAsync(before, cancellationToken);
        await operation.ValidateAsync(value, cancellationToken);
        return new(operation.Kind, operation.Target, before, value, operation.Risk, operation.RecoveryInstructions);
    }

    public Task<ChangeRecord> ApplyAsync(IReversibleOperation operation, string value,
        CancellationToken cancellationToken = default, string? approvedOriginal = null) =>
        Task.FromException<ChangeRecord>(AuditOnlyPolicy.ModificationNotSupported());

    public Task<ChangeRecord> RestoreAsync(Guid changeId, IReversibleOperation operation,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ChangeRecord>(AuditOnlyPolicy.ModificationNotSupported());
}
