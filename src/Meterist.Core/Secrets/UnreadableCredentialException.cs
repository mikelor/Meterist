namespace Meterist.Core.Secrets;

/// <summary>
/// The stored credentials for a tenant exist but cannot be read: the file will not decrypt (DPAPI ties decryption to
/// one Windows account on one machine, so this is what a scheduled task running as a different user looks like) or
/// its contents are damaged. It is deliberately not "no credential stored": that would let a run treat a tenant as
/// not using a vendor, skip it, and report success while nothing was collected.
/// <para>
/// The message names the tenant and the likely causes and never contains secret material. It derives from
/// <see cref="InvalidOperationException"/> so the callers that already report an operator-facing failure for that
/// type handle it without special cases.
/// </para>
/// </summary>
public sealed class UnreadableCredentialException : InvalidOperationException
{
    public UnreadableCredentialException(string tenantId, Exception inner)
        : base(
            $"The stored credentials for tenant '{tenantId}' could not be read. They are encrypted for one Windows account "
            + "on one machine, so this usually means Meterist is running as a different user, or on a different machine, "
            + "than the one that stored them (a scheduled task must run as the same account), or the file is damaged. "
            + "Run the tool as the original account, or store the credentials again with 'credentials set' for each vendor.",
            inner)
    {
        TenantId = tenantId;
    }

    public string TenantId { get; }
}
