using PCAnalyzer.Core;

namespace PCAnalyzer.Optimization;

public sealed record ChangePreview(string Kind, string Target, string Before, string After, ChangeRisk Risk,
    string RecoveryInstructions)
{
    public bool HasChange => !string.Equals(Before, After, StringComparison.Ordinal);
}

/// <summary>
/// Write-ahead reversible changes. The store must durably commit SaveChangeAsync before returning.
/// This engine does not grant permission, elevate itself, or decide when changes should run.
/// </summary>
public sealed class ReversibleChangeEngine(IHistoryStore history)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

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

    public async Task<ChangeRecord> ApplyAsync(IReversibleOperation operation, string value,
        CancellationToken cancellationToken = default, string? approvedOriginal = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var preview = await PreviewAsync(operation, value, cancellationToken);
            if (approvedOriginal is not null && !Equal(preview.Before, approvedOriginal))
                throw new InvalidOperationException("A configuração mudou desde a revisão confirmada. Leia e revise novamente; nenhuma escrita foi executada.");
            var now = DateTimeOffset.UtcNow;
            var change = new ChangeRecord(Guid.NewGuid(), preview.Kind, preview.Target, preview.Before, preview.After,
                preview.Risk, ChangeState.Prepared, now, now, RecoveryInstructions: preview.RecoveryInstructions);
            // Failure here must escape. It is never safe to write without a committed original value.
            await history.SaveChangeAsync(change, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var immediatelyBefore = await operation.ReadAsync(cancellationToken);
                if (!Equal(immediatelyBefore, change.Before))
                    throw new InvalidOperationException("Conflito: o valor mudou após a pré-visualização. Nenhuma escrita foi executada.");
                if (preview.HasChange)
                {
                    if (operation is IExpectedValueOperation expected)
                        await expected.WriteExpectedAsync(change.After, change.Before, cancellationToken);
                    else await operation.WriteAsync(change.After, cancellationToken);
                }
                // Once write begins, record actual outcome even if UI cancellation is requested.
                var actual = await operation.ReadAsync(CancellationToken.None);
                if (!Equal(actual, change.After))
                    throw new InvalidOperationException("Verificação após aplicação falhou: leitura real diverge do destino registrado.");
                change = change with { State = ChangeState.Applied, UpdatedAt = DateTimeOffset.UtcNow };
            }
            catch (Exception ex)
            {
                var observed = await ObserveFailureAsync(operation);
                change = change with
                {
                    State = ChangeState.Failed, UpdatedAt = DateTimeOffset.UtcNow,
                    Error = FailureMessage(ex) + observed,
                    RecoveryInstructions = operation.RecoveryInstructions +
                        " A escrita pode ter ocorrido parcialmente. Releia o estado e restaure pelo histórico; não presuma que nada mudou."
                };
            }
            await SaveOutcomeAsync(change);
            return change;
        }
        finally { _gate.Release(); }
    }

    public async Task<ChangeRecord> RestoreAsync(Guid changeId, IReversibleOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var change = await history.LoadChangeAsync(changeId, cancellationToken)
                ?? throw new ArgumentException("Alteração não encontrada no histórico.", nameof(changeId));
            if (change.Kind != operation.Kind || change.Target != operation.Target)
                throw new ArgumentException("A operação não corresponde ao componente e tipo do registro original.", nameof(operation));
            // Do not catch a failure to commit restore intent and proceed with a write.
            string actual;
            try { actual = await operation.ReadAsync(cancellationToken); }
            catch (Exception ex)
            {
                change = change with { State = ChangeState.RestoreFailed, UpdatedAt = DateTimeOffset.UtcNow,
                    Error = "A leitura antes da restauração falhou; nenhuma escrita foi executada. " + FailureMessage(ex) };
                await SaveOutcomeAsync(change);
                return change;
            }
            if (Equal(actual, change.Before))
            {
                change = change with { State = ChangeState.Restored, UpdatedAt = DateTimeOffset.UtcNow, Error = null };
                await SaveOutcomeAsync(change);
                return change;
            }
            if (!Equal(actual, change.After))
            {
                change = change with
                {
                    State = ChangeState.RestoreFailed, UpdatedAt = DateTimeOffset.UtcNow,
                    Error = "Conflito: estado atual é diferente do valor original e do aplicado. Restauração não sobrescreveu uma alteração externa."
                };
                await SaveOutcomeAsync(change);
                return change;
            }
            try
            {
                await operation.ValidateAsync(actual, cancellationToken);
                await operation.ValidateAsync(change.Before, cancellationToken);
            }
            catch (Exception ex)
            {
                change = change with { State = ChangeState.RestoreFailed, UpdatedAt = DateTimeOffset.UtcNow,
                    Error = FailureMessage(ex) + await ObserveFailureAsync(operation) };
                await SaveOutcomeAsync(change);
                return change;
            }
            change = change with
            {
                State = ChangeState.RestorePrepared, UpdatedAt = DateTimeOffset.UtcNow,
                Error = "Restauração preparada; resultado ainda não verificado.",
                RecoveryInstructions = operation.RecoveryInstructions +
                    " Restauração estava em andamento: releia o valor; Before é o original e After é o aplicado."
            };
            await history.SaveChangeAsync(change, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Equal(await operation.ReadAsync(cancellationToken), change.After))
                    throw new InvalidOperationException("Conflito: o estado mudou antes da restauração. Nenhuma escrita foi executada.");
                if (operation is IExpectedValueOperation expected)
                    await expected.WriteExpectedAsync(change.Before, change.After, cancellationToken);
                else await operation.WriteAsync(change.Before, cancellationToken);
                if (!Equal(await operation.ReadAsync(CancellationToken.None), change.Before))
                    throw new InvalidOperationException("A verificação real após restauração não confirmou o valor original.");
                change = change with { State = ChangeState.Restored, UpdatedAt = DateTimeOffset.UtcNow, Error = null };
            }
            catch (Exception ex)
            {
                change = change with { State = ChangeState.RestoreFailed, UpdatedAt = DateTimeOffset.UtcNow,
                    Error = FailureMessage(ex) + await ObserveFailureAsync(operation) };
            }
            await SaveOutcomeAsync(change);
            return change;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveOutcomeAsync(ChangeRecord change)
    {
        try { await history.SaveChangeAsync(change, CancellationToken.None); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"O resultado da alteração {change.Id} ({change.State}) não pôde ser persistido. " +
                "O registro preparado contém o backup; o estado real deve ser conferido antes de repetir a ação.", ex);
        }
    }
    private static string FailureMessage(Exception ex) => ex is OperationCanceledException
        ? "Operação cancelada; confira o estado real antes de repetir ou restaurar."
        : ex.GetType().Name + ": " + ex.Message;
    private static async Task<string> ObserveFailureAsync(IReversibleOperation operation)
    {
        try { return " Leitura real após falha: " + await operation.ReadAsync(CancellationToken.None); }
        catch (Exception ex) { return " Não foi possível verificar o estado real: " + ex.Message; }
    }
    private static bool Equal(string first, string second) => string.Equals(first, second, StringComparison.Ordinal);
}
