namespace Assessment.Api.Domain;

/// <summary>The tenant. Its own Id is the tenant key, so it is not ITenantOwned.</summary>
public class Organization : Entity
{
    public const decimal DefaultApprovalThreshold = 10_000m;

    public string Name { get; private set; } = null!;
    public decimal ApprovalThreshold { get; private set; } = DefaultApprovalThreshold;

    private Organization() { }

    public static Organization Create(string name, decimal? approvalThreshold = null) => new()
    {
        Name = name.Trim(),
        ApprovalThreshold = approvalThreshold ?? DefaultApprovalThreshold,
    };

    /// <summary>
    /// Applies only to requests created after the change: each request snapshots the threshold at creation.
    /// </summary>
    public void ChangeApprovalThreshold(decimal newThreshold, Guid actorUserId, string reason, DateTimeOffset now)
    {
        if (newThreshold < 0) throw new DomainException("Threshold cannot be negative.");
        if (newThreshold > Guard.MaxAmount) throw new DomainException($"Threshold must be at most {Guard.MaxAmount:N0}.");
        if (decimal.Round(newThreshold, 2) != newThreshold)
            throw new DomainException("Threshold can have at most 2 decimal places.");
        var validReason = Guard.Reason(reason);
        if (newThreshold == ApprovalThreshold) throw new DomainException("Threshold is unchanged.");

        var old = ApprovalThreshold;
        ApprovalThreshold = newThreshold;
        RaiseAudit(AuditEvent.Create(Id, nameof(Organization), Id, AuditActions.ThresholdChanged, actorUserId, now,
            new Dictionary<string, object?> { ["from"] = old, ["to"] = newThreshold, ["reason"] = validReason }));
    }
}
