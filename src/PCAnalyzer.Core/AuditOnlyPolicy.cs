namespace PCAnalyzer.Core;

/// <summary>The current product can observe system configuration but cannot change it.</summary>
public static class AuditOnlyPolicy
{
    public const string Explanation = "Esta versão é somente de auditoria e consultoria. " +
        "Aplicação e restauração de configurações do Windows estão desativadas. " +
        "Os registros anteriores continuam disponíveis para consulta.";

    public static NotSupportedException ModificationNotSupported() => new(Explanation);

    public static void ValidatePrivilegedAction(PrivilegedAction action)
    {
        // Keep historical enum values readable, without accepting them as executable verbs.
        if (action != PrivilegedAction.CaptureDpc) throw ModificationNotSupported();
    }
}
