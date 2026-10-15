namespace Proxyz.Core.Models;

public sealed record PendingChange
{
    public ProxySettings Before { get; init; } = new();
    public ProxySettings Intended { get; init; } = new();
    // Preserve the older recovery destination while journaling a recovery attempt.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ProxySettings? RecoveryTarget { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ChangeJournal
{
    public int SchemaVersion { get; init; } = 1;
    public ProxySettings? Original { get; init; }
    public ProxySettings? Previous { get; init; }
    public ProxySettings? LastApplied { get; init; }
    public Guid? LastProfileId { get; init; }
    public DateTimeOffset? LastChangeAt { get; init; }
    public PendingChange? Pending { get; init; }
}

public sealed record PolicyStatus(bool CanChange, string Reason, string Notice = "")
{
    public static PolicyStatus Allowed { get; } = new(true, "");
}

public sealed record OperationResult(bool Success, string Message, bool RecoveryRequired = false);
